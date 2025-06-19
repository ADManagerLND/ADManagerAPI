using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Utilities;
using ADManagerAPI.Utils;
using Microsoft.AspNetCore.SignalR;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ADManagerAPI.Services;

public partial class SpreadsheetImportService
{
    #region Analyse du fichier (CSV/Excel)

    public async Task<AnalysisResult> AnalyzeSpreadsheetContentAsync(Stream fileStream, string fileName,
        ImportConfig config, string? connectionId = null, CancellationToken cancellationToken = default)
    {
        if (fileStream == null || fileStream.Length == 0)
        {
            _logger.LogError("Le flux du fichier est vide ou null");
            return new AnalysisResult
            {
                Success = false,
                ErrorMessage = "Le fichier fourni est vide ou invalide."
            };
        }

        _logger.LogInformation($"🚀 Nouvelle analyse de fichier ({fileName}) démarrée");
        await SendProgressUpdateAsync(connectionId, 2, "initializing", "Initialisation de l'analyse...");

        try
        {
            await SendProgressUpdateAsync(connectionId, 5, "validating", "Validation de la configuration...");
            config = ImportConfigHelpers.EnsureValidConfig(config, _logger);

            await SendProgressUpdateAsync(connectionId, 8, "selecting-parser", "Sélection du parseur de fichier...");
            var parser = ChooseParser(fileName);

            if (parser == null)
            {
                _logger.LogError("Aucun service d'analyse de feuille de calcul n'a pu être déterminé.");
                return new AnalysisResult
                {
                    Success = false,
                    ErrorMessage =
                        "Aucun service d'analyse de feuille de calcul n'a pu être déterminé pour le type de fichier."
                };
            }

            await SendProgressUpdateAsync(connectionId, 12, "reading-file", $"Lecture du fichier {fileName}...");
            await SendProgressUpdateAsync(connectionId, 18, "parsing", "Extraction des données du fichier...");
            

            
            var spreadsheetData =
                await parser.ParseAsync(fileStream, fileName, config.CsvDelimiter, config.ManualColumns);

            if (spreadsheetData.Count == 0)
            {
                _logger.LogError("Aucune donnée valide trouvée dans le fichier.");
                return new AnalysisResult
                {
                    Success = false,
                    ErrorMessage = "Aucune donnée valide n'a été trouvée dans le fichier."
                };
            }

            await SendProgressUpdateAsync(connectionId, 25, "data-loaded", $"Fichier lu avec succès : {spreadsheetData.Count} lignes détectées");

            FileDataStore.SetCsvData(spreadsheetData);
            var analysisResult = await AnalyzeSpreadsheetDataAsync(spreadsheetData, config, connectionId, cancellationToken);

            if (analysisResult.Success && analysisResult.Analysis != null)
            {
                // 🔧 CORRECTION : Utiliser uniquement la méthode avec connectionId
                if (!string.IsNullOrEmpty(connectionId))
                {
                    AnalysisDataStore.SetAnalysis(connectionId, analysisResult.Analysis);
                    _logger.LogInformation(
                        $"✅ Analyse stockée pour connectionId: {connectionId}. Actions: {analysisResult.Analysis.Actions?.Count ?? 0}");
                }
                else
                {
                    _logger.LogWarning("\u26a0\ufe0f ConnectionId manquant, stockage dans AnalysisDataStore ignoré");
                }
            }

            await SendProgressUpdateAsync(connectionId, 100, "completed", "Analyse terminée");

            return analysisResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Erreur lors de l'analyse du contenu du fichier: {ex.Message}");
            await SendProgressUpdateAsync(connectionId, 0, "error", $"Erreur: {ex.Message}");
            return new AnalysisResult
            {
                Success = false,
                ErrorMessage = $"Une erreur est survenue lors de l'analyse du fichier: {ex.Message}"
            };
        }
    }

    public async Task<AnalysisResult> AnalyzeSpreadsheetDataAsync(List<Dictionary<string, string>> spreadsheetData,
        ImportConfig config, string? connectionId = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("🚀 Analyse de données de tableur déjà chargées");
        await SendProgressUpdateAsync(connectionId, 28, "preparing", "Préparation de l'analyse des données...");

        try
        {
            if (spreadsheetData == null || spreadsheetData.Count == 0)
            {
                _logger.LogError("Aucune donnée de tableur fournie pour l'analyse");
                return new AnalysisResult
                {
                    Success = false,
                    ErrorMessage = "Aucune donnée de tableur n'a été fournie pour l'analyse."
                };
            }

            await SendProgressUpdateAsync(connectionId, 32, "validating-config", "Validation de la configuration d'import...");
            config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
            
            await SendProgressUpdateAsync(connectionId, 35, "extracting-headers", "Extraction des en-têtes de colonnes...");
            var headers = spreadsheetData.FirstOrDefault()?.Keys.ToList() ?? new List<string>();
            var previewData = spreadsheetData.Take(10).ToList();

            await SendProgressUpdateAsync(connectionId, 38, "preparing-result", $"Structuration des données : {headers.Count} colonnes détectées");
            var result = new AnalysisResult
            {
                Success = true,
                CsvData = spreadsheetData,
                CsvHeaders = headers,
                PreviewData = previewData.Select(row => row as object).ToList(),
                TableData = spreadsheetData,
                Errors = new List<string>(),
                IsValid = true
            };

            await SendProgressUpdateAsync(connectionId, 42, "analyzing-actions", $"Analyse des actions nécessaires pour {spreadsheetData.Count} lignes...");
            var analysis =
                await AnalyzeSpreadsheetDataForActionsAsync(spreadsheetData, config, connectionId, cancellationToken);

            if (analysis != null)
            {
                result.Analysis = analysis;
                result.Summary = new
                {
                    TotalRows = spreadsheetData.Count,
                    ActionsCount = analysis.Actions.Count,
                    analysis.Summary.CreateCount,
                    analysis.Summary.UpdateCount,
                    analysis.Summary.ErrorCount
                };
            }
            else
            {
                _logger.LogWarning("L'analyse n'a généré aucun résultat");
                result.Errors.Add("L'analyse n'a généré aucun résultat");
                result.IsValid = false;
            }

            stopwatch.Stop();
            _logger.LogInformation($"✅ Analyse de données de tableur terminée en {stopwatch.ElapsedMilliseconds} ms");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Erreur lors de l'analyse des données de tableur: {ex.Message}");
            return new AnalysisResult
            {
                Success = false,
                ErrorMessage = $"Une erreur est survenue lors de l'analyse des données de tableur: {ex.Message}"
            };
        }
    }

    public async Task<ImportAnalysis> AnalyzeSpreadsheetDataForActionsAsync(
        List<Dictionary<string, string>> spreadsheetData, ImportConfig config, string? connectionId = null,
        CancellationToken cancellationToken = default)
    {

        const bool useOptimizedVersion = true; 

        if (useOptimizedVersion)
            try
            {
                _logger.LogInformation("🚀 Utilisation de la version optimisée de l'analyse ({Count} lignes)",
                    spreadsheetData.Count);
                return await AnalyzeSpreadsheetDataForActionsOptimizedAsync(spreadsheetData, config, connectionId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Erreur avec la version optimisée, fallback vers la version standard");
                // Fallback vers la version standard en cas d'erreur
            }
        
        _logger.LogInformation("📊 Utilisation de la version standard de l'analyse ({Count} lignes)",
            spreadsheetData.Count);
        return await AnalyzeSpreadsheetDataForActionsLegacyAsync(spreadsheetData, config, connectionId,
            cancellationToken);
    }

    /// <summary>
    ///     VERSION STANDARD (ancienne) conservée pour compatibilité et fallback
    /// </summary>
    public async Task<ImportAnalysis> AnalyzeSpreadsheetDataForActionsLegacyAsync(
        List<Dictionary<string, string>> spreadsheetData, ImportConfig config, string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
        var analysis = new ImportAnalysis
        {
            Summary = new ImportSummary { TotalObjects = spreadsheetData.Count },
            Actions = new List<ImportAction>()
        };
        var scannedOusForOrphanCleanup = new List<string>();

        try
        {
            await SendProgressUpdateAsync(connectionId, 48, "processing-ous", "Vérification des unités organisationnelles...");

            if (ShouldProcessOrganizationalUnits(config))
            {
                await SendProgressUpdateAsync(connectionId, 52, "analyzing-ous", "Analyse des OUs nécessaires...");
                await ProcessOrganizationalUnitsAsync(spreadsheetData, config, analysis, connectionId,
                    cancellationToken);
                await SendProgressUpdateAsync(connectionId, 58, "ous-processed", $"OUs analysées : {analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_OU)} à créer");
            }

            await SendProgressUpdateAsync(connectionId, 62, "processing-users", $"Traitement des {spreadsheetData.Count} utilisateurs...");
            await ProcessUsersAsync(spreadsheetData, config, analysis, connectionId, cancellationToken);
            
            var userActionsCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_USER || a.ActionType == ActionType.UPDATE_USER);
            await SendProgressUpdateAsync(connectionId, 78, "users-processed", $"Utilisateurs analysés : {userActionsCount} actions générées");

            if (IsOrphanCleanupEnabled(config))
            {
                await SendProgressUpdateAsync(connectionId, 82, "cleanup-orphans", "Recherche des utilisateurs orphelins...");
                scannedOusForOrphanCleanup =
                    await ProcessOrphanedUsersAsync(spreadsheetData, config, analysis, cancellationToken);

                var orphanCount = analysis.Actions.Count(a => a.ActionType == ActionType.DELETE_USER);
                await SendProgressUpdateAsync(connectionId, 88, "orphans-found", $"Utilisateurs orphelins trouvés : {orphanCount}");

                await SendProgressUpdateAsync(connectionId, 90, "cleanup-empty-groups", "Nettoyage des groupes vides...");
                await ProcessEmptyGroupsAsync(scannedOusForOrphanCleanup, config, analysis, cancellationToken);
                
                await SendProgressUpdateAsync(connectionId, 94, "cleanup-empty-ous", "Nettoyage des OUs vides...");
                await ProcessEmptyOrganizationalUnitsAsync(scannedOusForOrphanCleanup, config, analysis,
                    cancellationToken);
            }
            else
            {
                // ✅ Ce cas ne devrait plus jamais arriver puisque IsOrphanCleanupEnabled retourne toujours true
                _logger.LogWarning("⚠️ IsOrphanCleanupEnabled a retourné false - ceci ne devrait pas arriver");
            }

            await SendProgressUpdateAsync(connectionId, 96, "filtering-actions", "Application des filtres de configuration...");
            // Filtrer les actions désactivées selon la configuration
            FilterDisabledActions(analysis, config);
            
            await SendProgressUpdateAsync(connectionId, 98, "finalizing", "Finalisation de l'analyse...");
            UpdateAnalysisSummary(analysis);
            
            var totalActions = analysis.Actions.Count;
            await SendProgressUpdateAsync(connectionId, 100, "completed", $"Analyse terminée : {totalActions} actions planifiées");

            return analysis;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de l'analyse des données de tableur pour actions (version legacy)");
            throw;
        }
    }

    private void UpdateAnalysisSummary(ImportAnalysis analysis)
    {
        analysis.Summary.CreateCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_USER);
        analysis.Summary.UpdateCount = analysis.Actions.Count(a => a.ActionType == ActionType.UPDATE_USER);
        analysis.Summary.DeleteCount = analysis.Actions.Count(a => a.ActionType == ActionType.DELETE_USER);
        analysis.Summary.CreateOUCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_OU);
        analysis.Summary.DeleteOUCount = analysis.Actions.Count(a => a.ActionType == ActionType.DELETE_OU);
        analysis.Summary.MoveCount = analysis.Actions.Count(a => a.ActionType == ActionType.MOVE_USER);
        analysis.Summary.CreateStudentFolderCount =
            analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_STUDENT_FOLDER);
        analysis.Summary.CreateClassGroupFolderCount =
            analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_CLASS_GROUP_FOLDER);
        analysis.Summary.CreateTeamGroupCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_TEAM);
        
        // ✅ CORRECTION : ProvisionUserShareCount doit utiliser CREATE_STUDENT_FOLDER (c'est correct)
        // mais il ne faut pas dupliquer avec CreateStudentFolderCount
        analysis.Summary.ProvisionUserShareCount = analysis.Summary.CreateStudentFolderCount;
            
        // ✅ Ajout du comptage des groupes supprimés
        analysis.Summary.DeleteGroupCount = analysis.Actions.Count(a => a.ActionType == ActionType.DELETE_GROUP);
        
        // ✅ DÉBOGAGE AJOUTÉ : Log des compteurs pour diagnostiquer CREATE_STUDENT_FOLDER
        _logger.LogInformation($"📊 RÉSUMÉ ACTIONS:");
        _logger.LogInformation($"   • Utilisateurs - Créations: {analysis.Summary.CreateCount}, Mises à jour: {analysis.Summary.UpdateCount}, Suppressions: {analysis.Summary.DeleteCount}");
        _logger.LogInformation($"   • OUs - Créations: {analysis.Summary.CreateOUCount}, Suppressions: {analysis.Summary.DeleteOUCount}");
        _logger.LogInformation($"   • Dossiers étudiants (CREATE_STUDENT_FOLDER): {analysis.Summary.CreateStudentFolderCount}");
        _logger.LogInformation($"   • Groupes - Suppressions: {analysis.Summary.DeleteGroupCount}");
        _logger.LogInformation($"   • Mouvements: {analysis.Summary.MoveCount}");
        
        // ✅ DÉBOGAGE SPÉCIAL : Liste les actions CREATE_STUDENT_FOLDER s'il y en a
        var studentFolderActions = analysis.Actions.Where(a => a.ActionType == ActionType.CREATE_STUDENT_FOLDER).ToList();
        if (studentFolderActions.Any())
        {
            _logger.LogInformation($"📂 ACTIONS CREATE_STUDENT_FOLDER détectées ({studentFolderActions.Count}):");
            foreach (var action in studentFolderActions.Take(5)) // Limite à 5 pour éviter de spammer les logs
            {
                _logger.LogInformation($"     • {action.ObjectName} → {action.Path}");
            }
            if (studentFolderActions.Count > 5)
            {
                _logger.LogInformation($"     ... et {studentFolderActions.Count - 5} autres actions CREATE_STUDENT_FOLDER");
            }
        }
        else
        {
            _logger.LogWarning("❌ AUCUNE action CREATE_STUDENT_FOLDER détectée - Vérifiez la configuration Folders (TargetServerName, LocalPathForUserShareOnServer, ShareNameForUserFolders, NetBiosDomainName)");
        }
    }

    /// <summary>
    /// Filtre les actions en fonction des types d'actions désactivées dans la configuration
    /// </summary>
    private void FilterDisabledActions(ImportAnalysis analysis, ImportConfig config)
    {
        _logger.LogInformation($"🔍 FilterDisabledActions appelée - DisabledActionTypes: {(config.DisabledActionTypes?.Any() == true ? string.Join(", ", config.DisabledActionTypes) : "aucun")}");
        _logger.LogInformation($"🔍 Actions avant filtrage: {analysis.Actions.Count}");
        
        if (config.DisabledActionTypes == null || !config.DisabledActionTypes.Any())
        {
            _logger.LogInformation("✅ Aucune action désactivée - pas de filtrage nécessaire");
            return;
        }

        var originalCount = analysis.Actions.Count;
        
        // Log des actions avant filtrage par type
        var actionsByType = analysis.Actions.GroupBy(a => a.ActionType).ToDictionary(g => g.Key, g => g.Count());
        _logger.LogInformation($"📊 Actions par type avant filtrage: {string.Join(", ", actionsByType.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        
        // ✅ INFORMATION SPÉCIALE pour les utilisateurs orphelins
        var deleteUserCount = actionsByType.GetValueOrDefault(ActionType.DELETE_USER, 0);
        if (deleteUserCount > 0 && config.DisabledActionTypes.Contains(ActionType.DELETE_USER))
        {
            _logger.LogInformation($"🔍 {deleteUserCount} utilisateur(s) orphelin(s) détecté(s) mais action DELETE_USER désactivée - les suppressions ne seront pas exécutées");
        }
        
        // ✅ CORRECTION : Gérer la compatibilité entre enums entiers et chaînes de caractères
        var disabledActionTypesSet = new HashSet<string>();
        
        foreach (var disabledType in config.DisabledActionTypes)
        {
            // Ajouter le nom de l'enum (pour compatibilité avec le frontend)
            disabledActionTypesSet.Add(disabledType.ToString());
            
            // Ajouter la valeur entière (pour compatibilité directe)
            disabledActionTypesSet.Add(((int)disabledType).ToString());
        }
        
        _logger.LogInformation($"🔧 Types désactivés normalisés: {string.Join(", ", disabledActionTypesSet)}");
        
        // Filtrer les actions qui ne sont pas dans la liste des types désactivés
        analysis.Actions = analysis.Actions
            .Where(action => {
                var actionTypeStr = action.ActionType.ToString();
                var actionTypeInt = ((int)action.ActionType).ToString();
                
                // L'action est gardée si elle n'est PAS désactivée
                var isDisabled = disabledActionTypesSet.Contains(actionTypeStr) || 
                               disabledActionTypesSet.Contains(actionTypeInt);
                
                if (isDisabled)
                {
                    _logger.LogDebug($"🚫 Action filtrée: {action.ActionType} ({actionTypeStr}/{actionTypeInt}) - {action.ObjectName}");
                }
                
                return !isDisabled;
            })
            .ToList();

        var filteredCount = originalCount - analysis.Actions.Count;
        
        // Log des actions après filtrage par type
        var actionsAfterByType = analysis.Actions.GroupBy(a => a.ActionType).ToDictionary(g => g.Key, g => g.Count());
        _logger.LogInformation($"📊 Actions par type après filtrage: {string.Join(", ", actionsAfterByType.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        
        if (filteredCount > 0)
        {
            _logger.LogInformation($"🚫 {filteredCount} action(s) filtrée(s) selon la configuration " +
                                   $"(types désactivés: {string.Join(", ", config.DisabledActionTypes)})");
        }
        else
        {
            _logger.LogInformation("✅ Aucune action n'a été filtrée - les types désactivés ne correspondent à aucune action générée");
        }
    }

    #region Méthodes utilitaires pour SignalR

    /// <summary>
    ///     Envoie une mise à jour de progression via SignalR
    /// </summary>
    private async Task SendProgressUpdateAsync(string? connectionId, int progress, string status, string message)
    {
        if (!string.IsNullOrEmpty(connectionId) && _hubContext != null)
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("AnalysisProgress", new
                {
                    Progress = progress,
                    Status = status,
                    Message = message,
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Erreur lors de l'envoi de la progression SignalR pour {connectionId}");
            }
    }

    #endregion

    #region Méthodes utilitaires pour la configuration

    /// <summary>
    /// Vérifie si la suppression des utilisateurs orphelins est activée dans la configuration
    /// ✅ CORRECTION : Toujours permettre la DÉTECTION des orphelins, le filtrage se fait après
    /// </summary>
    private bool IsOrphanCleanupEnabled(ImportConfig config)
    {
        // ✅ NOUVELLE LOGIQUE : Toujours détecter les orphelins pour les afficher dans l'analyse
        // Le filtrage des actions se fera dans FilterDisabledActions si DELETE_USER est désactivé
        return true;  // Toujours détecter les orphelins
    }

    #endregion

    #region ✅ NOUVELLES MÉTHODES OPTIMISÉES POUR PERFORMANCE

    /// <summary>
    ///     Cache de comparaison pour éviter les recalculs
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _attributeComparisonCache = new();

    /// <summary>
    ///     Version optimisée de AnalyzeSpreadsheetDataForActionsAsync avec pré-chargement et gestion des doublons
    /// </summary>
    public async Task<ImportAnalysis> AnalyzeSpreadsheetDataForActionsOptimizedAsync(
        List<Dictionary<string, string>> spreadsheetData,
        ImportConfig config,
        string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("🚀 Analyse optimisée de {Count} lignes démarrée avec gestion des doublons", spreadsheetData.Count);

        config = ImportConfigHelpers.EnsureValidConfig(config, _logger);

        var analysis = new ImportAnalysis
        {
            Summary = new ImportSummary { TotalObjects = spreadsheetData.Count },
            Actions = new List<ImportAction>()
        };

        try
        {
            // 1. ✅ PRÉ-CHARGEMENT de toutes les données LDAP nécessaires avec résolution des doublons
            await SendProgressUpdateAsync(connectionId, 45, "preloading-ldap", "Pré-chargement des données LDAP et résolution des doublons...");
            var cache = await PreloadUserDataAsync(spreadsheetData, config, connectionId, cancellationToken);

            // 2. Traitement des OUs (si nécessaire)
            if (ShouldProcessOrganizationalUnits(config))
            {
                await SendProgressUpdateAsync(connectionId, 58, "processing-ous-optimized",
                    "Analyse optimisée des unités organisationnelles...");
                await ProcessOrganizationalUnitsOptimizedAsync(spreadsheetData, config, analysis, cache, connectionId,
                    cancellationToken);
                
                var ouActionsCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_OU);
                await SendProgressUpdateAsync(connectionId, 62, "ous-optimized-done", $"OUs optimisées : {ouActionsCount} créations planifiées");
            }

            // 3. ✅ TRAITEMENT OPTIMISÉ des utilisateurs avec gestion des doublons
            await SendProgressUpdateAsync(connectionId, 65, "processing-users-optimized", $"Traitement optimisé de {spreadsheetData.Count} utilisateurs (doublons résolus)...");
            await ProcessUsersOptimizedAsync(spreadsheetData, config, analysis, cache, connectionId, cancellationToken);

            // 4. Nettoyage optimisé des orphelins
            if (IsOrphanCleanupEnabled(config))
            {
                await SendProgressUpdateAsync(connectionId, 82, "cleanup-orphans-optimized", "Recherche optimisée des utilisateurs orphelins...");
                var scannedOus =
                    await ProcessOrphanedUsersOptimizedAsync(spreadsheetData, config, analysis, cache,
                        cancellationToken);

                var orphanCount = analysis.Actions.Count(a => a.ActionType == ActionType.DELETE_USER);
                await SendProgressUpdateAsync(connectionId, 88, "orphans-optimized-found", $"Recherche terminée : {orphanCount} utilisateurs orphelins");

                await SendProgressUpdateAsync(connectionId, 92, "cleanup-empty-ous-optimized", "Nettoyage optimisé des OUs vides...");
                await ProcessEmptyOrganizationalUnitsAsync(scannedOus, config, analysis, cancellationToken);
            }
            else
            {
                // ✅ Ce cas ne devrait plus jamais arriver puisque IsOrphanCleanupEnabled retourne toujours true
                _logger.LogWarning("⚠️ IsOrphanCleanupEnabled a retourné false - ceci ne devrait pas arriver");
            }

            await SendProgressUpdateAsync(connectionId, 96, "filtering-actions-optimized", "Application des filtres...");
            // Filtrer les actions désactivées selon la configuration
            FilterDisabledActions(analysis, config);
            
            await SendProgressUpdateAsync(connectionId, 98, "finalizing-optimized", "Finalisation optimisée...");
            UpdateAnalysisSummary(analysis);
            stopwatch.Stop();

            var finalSummary = $"✅ Analyse terminée en {stopwatch.ElapsedMilliseconds}ms : " +
                              $"{analysis.Summary.CreateCount} créations, " +
                              $"{analysis.Summary.UpdateCount} modifications, " +
                              $"{analysis.Summary.DeleteCount} suppressions";
            
            await SendProgressUpdateAsync(connectionId, 100, "completed", finalSummary);

            _logger.LogInformation($"✅ Analyse optimisée terminée en {stopwatch.ElapsedMilliseconds} ms " +
                                   $"({analysis.Actions.Count} actions générées) - Doublons résolus automatiquement");

            return analysis;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de l'analyse optimisée des données");
            throw;
        }
    }

    /// <summary>
    ///     ✅ PRÉ-CHARGE toutes les données LDAP nécessaires en lot pour éviter les appels répétés
    ///     🆕 GESTION DES DOUBLONS : Détecte et résout les conflits de sAMAccountName
    /// </summary>
    private async Task<UserAnalysisCache> PreloadUserDataAsync(
        List<Dictionary<string, string>> spreadsheetData,
        ImportConfig config,
        string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var cache = new UserAnalysisCache();
        var sw = Stopwatch.StartNew();

        // 1. Extraire tous les sAMAccountNames avec gestion des doublons
        var samAccountMapping = ResolveDuplicateSamAccountNames(spreadsheetData, config);
        var allSamAccountNames = samAccountMapping.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _logger.LogInformation(
            $"🔍 Extraction de {allSamAccountNames.Count} sAMAccountNames uniques depuis {spreadsheetData.Count} lignes (doublons résolus)");

        // Stocker le mapping des doublons dans le cache pour usage ultérieur
        cache.SamAccountMapping = samAccountMapping;

        // 2. ✅ PRÉ-CHARGER tous les utilisateurs existants en une seule requête LDAP
        if (allSamAccountNames.Any())
        {
            await SendProgressUpdateAsync(connectionId, 48, "loading-users-batch",
                $"Chargement batch de {allSamAccountNames.Count} utilisateurs...");

            try
            {
                var existingUsers = await _ldapService.GetUsersBatchAsync(allSamAccountNames);
                cache.ExistingUsers = existingUsers.ToDictionary(
                    u => u.SamAccountName,
                    u => u,
                    StringComparer.OrdinalIgnoreCase);

                cache.Statistics.TotalUsersLoaded = cache.ExistingUsers.Count;
                await SendProgressUpdateAsync(connectionId, 52, "users-loaded",
                    $"✓ {cache.ExistingUsers.Count} utilisateurs chargés depuis LDAP");
                _logger.LogInformation(
                    $"📥 {cache.ExistingUsers.Count} utilisateurs existants chargés depuis LDAP en une seule requête");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "⚠️ Erreur lors du chargement batch des utilisateurs, fallback vers méthode standard");
                await SendProgressUpdateAsync(connectionId, 50, "users-fallback",
                    "Chargement individuel des utilisateurs...");
                // Fallback vers la méthode actuelle si la méthode batch n'est pas disponible
                await PreloadUsersIndividuallyAsync(allSamAccountNames, cache);
            }
        }

        // 3. ✅ PRÉ-CHARGER toutes les OUs nécessaires
        var allOuPaths = spreadsheetData
            .AsParallel()
            .Select(row => DetermineUserOuPath(row, config))
            .Where(ouPath => !string.IsNullOrEmpty(ouPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allOuPaths.Any())
        {
            await SendProgressUpdateAsync(connectionId, 54, "loading-ous-batch",
                $"Vérification batch de {allOuPaths.Count} OUs...");

            try
            {
                var existingOus = await _ldapService.GetOrganizationalUnitsBatchAsync(allOuPaths);
                cache.ExistingOUs = existingOus.ToHashSet(StringComparer.OrdinalIgnoreCase);

                cache.Statistics.TotalOUsLoaded = cache.ExistingOUs.Count;
                await SendProgressUpdateAsync(connectionId, 56, "ous-loaded",
                    $"✓ {cache.ExistingOUs.Count} OUs trouvées sur {allOuPaths.Count} vérifiées");
                _logger.LogInformation(
                    $"📁 {cache.ExistingOUs.Count} OUs existantes trouvées sur {allOuPaths.Count} vérifiées en une seule requête");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Erreur lors du chargement batch des OUs, fallback vers méthode standard");
                await SendProgressUpdateAsync(connectionId, 55, "ous-fallback",
                    "Chargement individuel des OUs...");
                // Fallback vers la méthode actuelle
                await PreloadOUsIndividuallyAsync(allOuPaths, cache);
            }
        }

        sw.Stop();
        cache.Statistics.LoadTime = sw.Elapsed;
        _logger.LogInformation(
            $"⚡ Pré-chargement terminé en {sw.ElapsedMilliseconds} ms (gain estimé: {Math.Round((double)(allSamAccountNames.Count + allOuPaths.Count) * 50 / 1000, 1)}s)");

        return cache;
    }

    /// <summary>
    ///     🆕 NOUVELLE MÉTHODE : Résout les doublons en modifiant les données sources pour répercuter sur tous les attributs
    /// </summary>
    private Dictionary<string, string> ResolveDuplicateSamAccountNames(
        List<Dictionary<string, string>> spreadsheetData, 
        ImportConfig config)
    {
        var samAccountMapping = new Dictionary<string, string>();
        var usedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < spreadsheetData.Count; i++)
        {
            var row = spreadsheetData[i];
            var originalMappedRow = MapRow(row, config);
            var originalSamAccountName = originalMappedRow.GetValueOrDefault("sAMAccountName")?.Trim();

            if (string.IsNullOrEmpty(originalSamAccountName))
            {
                _logger.LogWarning($"⚠️ Ligne {i + 1}: sAMAccountName manquant après mapping");
                continue;
            }

            var rowKey = $"Row_{i}";
            
            // Créer une identité unique basée sur prénom + nom
            var prenom = row.ContainsKey("prenom") ? row["prenom"] : row.ContainsKey("Prenom") ? row["Prenom"] : "";
            var nom = row.ContainsKey("nom") ? row["nom"] : row.ContainsKey("Nom") ? row["Nom"] : "";
            var identity = $"{prenom?.Trim()?.ToLowerInvariant()}.{nom?.Trim()?.ToLowerInvariant()}";

            // Vérifier si cette identité (prénom.nom) est déjà utilisée
            if (usedIdentities.Contains(identity))
            {
                // Initialiser le compteur pour cette identité
                if (!duplicateCounters.ContainsKey(identity))
                {
                    duplicateCounters[identity] = 0;
                }

                // Incrémenter le compteur
                duplicateCounters[identity]++;
                var suffix = duplicateCounters[identity];

                // 🆕 MODIFIER LES DONNÉES SOURCES : Ajouter le suffixe au nom de famille
                var modifiedRow = new Dictionary<string, string>(row);
                if (modifiedRow.ContainsKey("nom"))
                {
                    modifiedRow["nom"] = $"{nom}{suffix}";
                }
                else if (modifiedRow.ContainsKey("Nom"))
                {
                    modifiedRow["Nom"] = $"{nom}{suffix}";
                }

                // Re-mapper avec les données modifiées pour que tous les attributs se mettent à jour
                var newMappedRow = MapRow(modifiedRow, config);
                var finalSamAccountName = newMappedRow.GetValueOrDefault("sAMAccountName")?.Trim();

                // Remplacer les données dans la liste originale
                spreadsheetData[i] = modifiedRow;

                var classe = row.ContainsKey("classe") ? row["classe"] : 
                           row.ContainsKey("Classe") ? row["Classe"] : "";

                _logger.LogInformation(
                    $"🔄 Doublon détecté ligne {i + 1}: {prenom} {nom} → {prenom} {nom}{suffix} (classe: {classe}) - " +
                    $"Tous les attributs mis à jour automatiquement");

                samAccountMapping[rowKey] = finalSamAccountName ?? originalSamAccountName;
            }
            else
            {
                // Pas de doublon, utiliser les données originales
                samAccountMapping[rowKey] = originalSamAccountName;
            }

            usedIdentities.Add(identity);
        }

        // Log des statistiques de doublons
        var duplicateStats = duplicateCounters.Where(kvp => kvp.Value > 1).ToList();
        if (duplicateStats.Any())
        {
            _logger.LogInformation($"📊 Statistiques des doublons résolus:");
            foreach (var stat in duplicateStats)
            {
                _logger.LogInformation($"   • '{stat.Key}': {stat.Value} occurrences");
            }
        }

        return samAccountMapping;
    }

    /// <summary>
    ///     🆕 Génère un sAMAccountName unique avec suffixe numérique
    /// </summary>
    private string GenerateUniqueSamAccountName(string baseName, int suffix, HashSet<string> usedNames)
    {
        const int maxLength = 20; // Limite AD pour sAMAccountName
        var suffixStr = suffix.ToString();
        var maxBaseLength = maxLength - suffixStr.Length;

        // Tronquer le nom de base si nécessaire
        var truncatedBase = baseName.Length > maxBaseLength 
            ? baseName.Substring(0, maxBaseLength) 
            : baseName;

        var candidateName = $"{truncatedBase}{suffixStr}";

        // Si le nom généré est encore en conflit, essayer avec un suffixe plus grand
        var counter = suffix;
        while (usedNames.Contains(candidateName) && counter < 999)
        {
            counter++;
            suffixStr = counter.ToString();
            maxBaseLength = maxLength - suffixStr.Length;
            truncatedBase = baseName.Length > maxBaseLength 
                ? baseName.Substring(0, maxBaseLength) 
                : baseName;
            candidateName = $"{truncatedBase}{suffixStr}";
        }

        return candidateName;
    }

    /// <summary>
    ///     Fallback pour le chargement individuel des utilisateurs
    /// </summary>
    private async Task PreloadUsersIndividuallyAsync(List<string> samAccountNames, UserAnalysisCache cache)
    {
        var semaphore = new SemaphoreSlim(10); // Limiter la concurrence
        var foundUsers = new ConcurrentBag<UserModel>();

        var userTasks = samAccountNames.Select(async sam =>
        {
            await semaphore.WaitAsync();
            try
            {
                var user = await _ldapService.GetUserAsync(sam); // Utiliser GetUserAsync pour obtenir UserModel complet
                if (user != null) foundUsers.Add(user);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(userTasks);

        // Stocker les UserModel complets
        foreach (var user in foundUsers) cache.ExistingUsers[user.SamAccountName] = user;

        cache.Statistics.TotalUsersLoaded = cache.ExistingUsers.Count;
        semaphore.Dispose();
        _logger.LogInformation($"📥 Fallback: {cache.ExistingUsers.Count} utilisateurs chargés individuellement");
    }

    /// <summary>
    ///     Fallback pour le chargement individuel des OUs
    /// </summary>
    private async Task PreloadOUsIndividuallyAsync(List<string> ouPaths, UserAnalysisCache cache)
    {
        var semaphore = new SemaphoreSlim(10);
        var foundOUs = new ConcurrentBag<string>();

        var ouTasks = ouPaths.Select(async ouPath =>
        {
            await semaphore.WaitAsync();
            try
            {
                var exists = await CheckOrganizationalUnitExistsAsync(ouPath);
                if (exists) foundOUs.Add(ouPath);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(ouTasks);

        cache.ExistingOUs = foundOUs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        cache.Statistics.TotalOUsLoaded = cache.ExistingOUs.Count;
        semaphore.Dispose();
        _logger.LogInformation($"📁 Fallback: {cache.ExistingOUs.Count} OUs chargées individuellement");
    }

    /// <summary>
    ///     Version optimisée du traitement des utilisateurs SANS appels LDAP répétés
    /// </summary>
    private async Task ProcessUsersOptimizedAsync(
        List<Dictionary<string, string>> spreadsheetData,
        ImportConfig config,
        ImportAnalysis analysis,
        UserAnalysisCache cache,
        string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var ousToBeCreated = new ConcurrentHashSet<string>(
            analysis.Actions.Where(a => a.ActionType == ActionType.CREATE_OU).Select(a => a.Path),
            StringComparer.OrdinalIgnoreCase);

        var userActions = new ConcurrentBag<ImportAction>();
        var totalRows = spreadsheetData.Count;
        var processedRows = 0;
        var skippedRows = 0;

        // ✅ TRAITEMENT EN PARALLÈLE OPTIMISÉ avec index de ligne pour la gestion des doublons
        await Parallel.ForEachAsync(spreadsheetData.Select((row, index) => new { Row = row, Index = index }),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 4, 32), // Optimisé pour I/O
                CancellationToken = cancellationToken
            },
            async (item, ct) =>
            {
                var actions = await ProcessUserRowOptimizedAsync(item.Row, config, ousToBeCreated, cache, item.Index);

                if (actions.Any())
                    foreach (var action in actions)
                        userActions.Add(action);
                else
                    Interlocked.Increment(ref skippedRows);

                var completed = Interlocked.Increment(ref processedRows);
                if (completed % 50 == 0 || completed == totalRows) // Mise à jour plus fréquente
                {
                    var progress = 65 + completed * 15 / totalRows; // 65-80% pour les utilisateurs
                    var currentActions = userActions.Count;
                    await SendProgressUpdateAsync(connectionId, progress, "processing-users-progress",
                        $"Traitement... {completed}/{totalRows} lignes • {currentActions} actions générées");
                }
            });

        analysis.Actions.AddRange(userActions);

        await SendProgressUpdateAsync(connectionId, 80, "users-processing-complete",
            $"✓ Utilisateurs traités : {userActions.Count} actions • {skippedRows} lignes sans modification");

        _logger.LogInformation($"🚀 {userActions.Count} actions utilisateur générées pour {totalRows} lignes " +
                               $"({skippedRows} lignes ignorées car aucune action nécessaire)");
    }

    /// <summary>
    ///     ✅ VERSION OPTIMISÉE du traitement d'une ligne utilisateur (avec vérifications asynchrones)
    /// </summary>
    private async Task<List<ImportAction>> ProcessUserRowOptimizedAsync(
        Dictionary<string, string> row,
        ImportConfig config,
        ConcurrentHashSet<string> ousToBeCreated,
        UserAnalysisCache cache,
        int rowIndex = -1)
    {
        var mappedRow = MapRow(row, config);
        var originalSamAccountName = mappedRow.GetValueOrDefault("sAMAccountName");

        // 🆕 GESTION DES DOUBLONS : Utiliser le sAMAccountName résolu depuis le cache
        var samAccountName = originalSamAccountName;
        if (rowIndex >= 0 && cache.SamAccountMapping != null)
        {
            var rowKey = $"Row_{rowIndex}";
            if (cache.SamAccountMapping.TryGetValue(rowKey, out var resolvedSamAccountName))
            {
                samAccountName = resolvedSamAccountName;
                
                // Mettre à jour les attributs mappés avec le sAMAccountName résolu
                mappedRow["sAMAccountName"] = samAccountName;
                
                // Log si le sAMAccountName a été modifié pour résoudre un doublon
                if (!string.Equals(originalSamAccountName, samAccountName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogDebug($"🔄 Doublon résolu pour ligne {rowIndex + 1}: '{originalSamAccountName}' → '{samAccountName}'");
                }
            }
        }

        _logger?.LogDebug($"🔍 ProcessUserRowOptimized - sAMAccountName final: '{samAccountName}'");
        _logger?.LogDebug($"🔍 Ligne CSV originale: {string.Join(", ", row.Select(kvp => $"{kvp.Key}='{kvp.Value}'"))}");

        if (string.IsNullOrEmpty(samAccountName))
        {
            _logger?.LogError($"❌ sAMAccountName manquant après mapping. Ligne: {string.Join(", ", row.Take(3).Select(kvp => $"{kvp.Key}='{kvp.Value}'"))}...");
            return new List<ImportAction>
            {
                new()
                {
                    ActionType = ActionType.ERROR,
                    ObjectName = $"Ligne_erreur_{Guid.NewGuid().ToString("N")[..8]}", // ID unique pour identifier
                    Path = config.DefaultOU,
                    Message = $"sAMAccountName manquant dans les données mappées. Vérifiez la configuration du mapping des colonnes.",
                    Attributes = new Dictionary<string, string>(row) // Conserver les données originales pour debug
                    {
                        ["MappedData"] = string.Join("; ", mappedRow.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                    }
                }
            };
        }

        var cleanedSamAccountName = samAccountName.Trim();
        _logger?.LogDebug($"✅ sAMAccountName nettoyé: '{cleanedSamAccountName}'");
        var actions = new List<ImportAction>();
        var ouPath = DetermineUserOuPath(mappedRow, config);

        // ✅ VÉRIFICATION OU depuis le cache (SANS appel LDAP)
        var ouExists = ousToBeCreated.Contains(ouPath) || cache.ExistingOUs.Contains(ouPath);

        if (!ouExists)
        {
            if (config.CreateMissingOUs)
            {
                ousToBeCreated.Add(ouPath);
                ouExists = true;
            }
            else
            {
                _logger.LogWarning(
                    $"OU '{ouPath}' n'existe pas, utilisation de l'OU par défaut pour '{cleanedSamAccountName}'");
                ouPath = config.DefaultOU;
            }
        }


        var userExists = cache.ExistingUsers.ContainsKey(cleanedSamAccountName);

        var userActionType = ActionType.ERROR;
        string userActionMessage = null;
        var shouldAddAction = true;

        if (userExists)
        {
            // ✨ Vérifier si l'utilisateur doit être déplacé (version optimisée)
            var existingUser = cache.ExistingUsers[cleanedSamAccountName];
            var currentOu = existingUser.OrganizationalUnit;

            if (!string.IsNullOrEmpty(currentOu) &&
                !string.Equals(currentOu, ouPath, StringComparison.OrdinalIgnoreCase))
            {
                // L'utilisateur doit être déplacé
                userActionType = ActionType.MOVE_USER;
                userActionMessage = $"Déplacement nécessaire : de '{currentOu}' vers '{ouPath}'";
                mappedRow["SourceOU"] = currentOu;
                cache.Statistics.CacheHits++;
            }
            else if (string.IsNullOrEmpty(currentOu))
            {
                var hasChanges = HasAttributeChangesOptimized(mappedRow, cleanedSamAccountName, cache);

                if (hasChanges)
                {
                    userActionType = ActionType.UPDATE_USER;
                    userActionMessage = "Mise à jour nécessaire : différences détectées dans les attributs";
                    cache.Statistics.CacheHits++;
                }
                else
                {
                    shouldAddAction = false;
                    cache.Statistics.CacheHits++;
                }
            }
            else
            {
                // Vérifier les changements d'attributs
                var hasChanges = HasAttributeChangesOptimized(mappedRow, cleanedSamAccountName, cache);

                if (hasChanges)
                {
                    userActionType = ActionType.UPDATE_USER;
                    userActionMessage = "Mise à jour nécessaire : différences détectées dans les attributs";
                    cache.Statistics.CacheHits++;
                }
                else
                {
                    shouldAddAction = false;
                    cache.Statistics.CacheHits++;
                }
            }
        }
        else
        {
            userActionType = ActionType.CREATE_USER;
            userActionMessage = "Création d'un nouvel utilisateur";
            cache.Statistics.CacheMisses++;
        }

        // Ajouter l'action uniquement si nécessaire
        if (shouldAddAction)
        {
            var userAttributes = new Dictionary<string, string>(mappedRow);
            actions.Add(new ImportAction
            {
                ActionType = userActionType,
                ObjectName = cleanedSamAccountName,
                Path = ouPath,
                Message = userActionMessage,
                Attributes = userAttributes
            });
        }

        // Actions supplémentaires (avec vérifications asynchrones)
        var additionalActions = await ProcessAdditionalUserActionsOptimizedAsync(mappedRow, config, cleanedSamAccountName, ouPath);
        actions.AddRange(additionalActions);

        return actions;
    }

    /// <summary>
    ///     ✅ COMPARAISON OPTIMISÉE des attributs avec cache intelligent
    /// </summary>
    private bool HasAttributeChangesOptimized(
        Dictionary<string, string> mappedRow,
        string samAccountName,
        UserAnalysisCache cache)
    {
        var attributesForComparison = PrepareAttributesForComparison(mappedRow);

        // Créer une clé de cache basée sur les attributs
        var attributeHash = string.Join("|",
            attributesForComparison.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
        var cacheKey = $"{samAccountName}:{attributeHash.GetHashCode()}";

        return _attributeComparisonCache.GetOrAdd(cacheKey, _ =>
        {
            // Si nous avons les attributs existants dans le cache, les utiliser
            if (cache.UserAttributes.TryGetValue(samAccountName, out var existingAttributes))
                return HasAttributeChanges(attributesForComparison, existingAttributes, samAccountName);

            // Si nous avons l'utilisateur en cache, utiliser TOUS ses attributs
            if (cache.ExistingUsers.ContainsKey(samAccountName))
            {
                var ldapUser = cache.ExistingUsers[samAccountName];
                
                // ✅ CORRECTION CRITIQUE : Utiliser TOUS les attributs depuis AdditionalAttributes
                var allExistingAttributes = new Dictionary<string, string?>();
                
                // Ajouter les attributs de base
                if (!string.IsNullOrEmpty(ldapUser.DisplayName)) allExistingAttributes["displayName"] = ldapUser.DisplayName;
                if (!string.IsNullOrEmpty(ldapUser.GivenName)) allExistingAttributes["givenName"] = ldapUser.GivenName;
                if (!string.IsNullOrEmpty(ldapUser.Surname)) allExistingAttributes["sn"] = ldapUser.Surname;
                if (!string.IsNullOrEmpty(ldapUser.UserPrincipalName)) allExistingAttributes["userPrincipalName"] = ldapUser.UserPrincipalName;

                // ✅ CRITIQUE : Ajouter TOUS les attributs depuis AdditionalAttributes (insensible à la casse)
                if (ldapUser.AdditionalAttributes != null)
                {
                    foreach (var kvp in ldapUser.AdditionalAttributes)
                    {
                        // Utiliser une clé insensible à la casse pour éviter les doublons
                        var normalizedKey = kvp.Key.ToLowerInvariant();
                        allExistingAttributes[normalizedKey] = kvp.Value;
                    }
                }



                return HasAttributeChanges(attributesForComparison, allExistingAttributes, samAccountName);
            }

            // Sinon, assumer qu'une mise à jour est nécessaire par sécurité
            _logger.LogDebug($"Pas d'attributs en cache pour {samAccountName}, mise à jour prévue par sécurité");
            return true;
        });
    }

    /// <summary>
    ///     Version optimisée du nettoyage des orphelins
    /// </summary>
    private async Task<List<string>> ProcessOrphanedUsersOptimizedAsync(
        List<Dictionary<string, string>> spreadsheetData,
        ImportConfig config,
        ImportAnalysis analysis,
        UserAnalysisCache cache,
        CancellationToken cancellationToken = default)
    {
        // ✅ CORRECTION : Toujours détecter les orphelins, le filtrage se fait plus tard
        // Note: IsOrphanCleanupEnabled retourne maintenant toujours true pour permettre la détection
        if (!IsOrphanCleanupEnabled(config))
        {
            _logger.LogWarning("⚠️ IsOrphanCleanupEnabled a retourné false - ceci ne devrait pas arriver");
            return new List<string>();
        }

        var rootOuForCleanup = config.DefaultOU;

        if (string.IsNullOrEmpty(rootOuForCleanup))
        {
            analysis.Actions.Add(new ImportAction
            {
                ActionType = ActionType.ERROR,
                ObjectName = "Config Nettoyage Orphelins",
                Message = "config.DefaultOU non configurée."
            });
            return new List<string>();
        }

        // ✅ UTILISER les données déjà chargées du fichier avec nettoyage cohérent
        var spreadsheetUsers = spreadsheetData
            .AsParallel()
            .Select(row => MapRow(row, config))
            .Where(mapped => mapped.ContainsKey("sAMAccountName") && !string.IsNullOrEmpty(mapped["sAMAccountName"]))
            .Select(mapped => {
                var samAccountName = mapped["sAMAccountName"].Trim();
                // ✅ Nettoyer de la même façon que dans la version legacy : supprimer tout après '('
                var cleanedSam = samAccountName.Split('(')[0].Trim();
                return cleanedSam;
            })
            .Where(cleaned => !string.IsNullOrEmpty(cleaned))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation($"🔍 {spreadsheetUsers.Count} utilisateurs du fichier pour comparaison orphelins");
        
        // ✅ Log de debug pour voir les utilisateurs extraits du fichier
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug($"📝 Utilisateurs extraits du fichier: {string.Join(", ", spreadsheetUsers.Take(10))}");
            if (spreadsheetUsers.Count > 10)
                _logger.LogDebug($"... et {spreadsheetUsers.Count - 10} autres utilisateurs");
        }

        try
        {
            // ✅ APPROCHE BATCH ULTRA-RAPIDE : Récupérer seulement les sAMAccountNames
            _logger.LogInformation($"⚡ Récupération batch optimisée des sAMAccountNames dans '{rootOuForCleanup}'");
            var allSamAccountNamesInAD = await _ldapService.GetAllSamAccountNamesInOuBatchAsync(rootOuForCleanup);
            _logger.LogInformation($"📋 {allSamAccountNamesInAD.Count} sAMAccountNames trouvés dans l'AD sous '{rootOuForCleanup}'");

            // ✅ COMPARAISON DIRECTE O(n) : Comparer les listes de noms
            var orphanedSamAccountNames = allSamAccountNamesInAD
                .AsParallel()
                .Where(samAD => !spreadsheetUsers.Contains(samAD))
                .ToList();

            _logger.LogInformation($"🗑️ {orphanedSamAccountNames.Count} utilisateurs orphelins détectés par comparaison batch");
            
            // ✅ Log de debug pour voir les orphelins
            if (orphanedSamAccountNames.Any() && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug($"🗑️ Utilisateurs orphelins détectés (batch): {string.Join(", ", orphanedSamAccountNames.Take(10))}");
                if (orphanedSamAccountNames.Count > 10)
                    _logger.LogDebug($"... et {orphanedSamAccountNames.Count - 10} autres utilisateurs orphelins");
            }

            // ✅ RÉCUPÉRER les données complètes seulement pour les orphelins identifiés
            List<UserModel> orphanedUsers = new List<UserModel>();
            if (orphanedSamAccountNames.Any())
            {
                _logger.LogInformation($"📥 Récupération des données complètes pour {orphanedSamAccountNames.Count} orphelins...");
                orphanedUsers = await _ldapService.GetUsersBatchAsync(orphanedSamAccountNames);
                _logger.LogInformation($"✅ Données complètes récupérées pour {orphanedUsers.Count} orphelins");
            }

            // ✅ Actions de suppression basées sur les orphelins identifiés

            // Ajouter les actions de suppression
            var deleteActions = orphanedUsers.Select(orphanUser => new ImportAction
            {
                ActionType = ActionType.DELETE_USER,
                ObjectName = orphanUser.SamAccountName,
                Path = orphanUser.OrganizationalUnit,
                Message =
                    $"Suppression de l'utilisateur orphelin '{orphanUser.SamAccountName}' (non présent dans le fichier)",
                Attributes = new Dictionary<string, string>
                {
                    ["DistinguishedName"] =
                        orphanUser.AdditionalAttributes?.GetValueOrDefault("distinguishedName") ?? "",
                    ["DisplayName"] = orphanUser.DisplayName ?? ""
                }
            }).ToList();

            analysis.Actions.AddRange(deleteActions);
            
            _logger.LogInformation($"🎯 RÉSUMÉ DÉTECTION ORPHELINS: {allSamAccountNamesInAD.Count} utilisateurs dans AD, {spreadsheetUsers.Count} dans fichier, {orphanedSamAccountNames.Count} orphelins détectés, {deleteActions.Count} actions de suppression ajoutées");

            // ✅ Retourner l'OU racine scannée pour le nettoyage des OUs vides
            return new List<string> { rootOuForCleanup };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors du processus optimisé de nettoyage des orphelins");
            analysis.Actions.Add(new ImportAction
            {
                ActionType = ActionType.ERROR,
                ObjectName = "Nettoyage Orphelins",
                Message = $"Erreur: {ex.Message}"
            });
            return new List<string>();
        }
    }

    /// <summary>
    ///     Version optimisée du traitement des OUs avec cache - UTILISE LA MÉTHODE CENTRALISÉE
    /// </summary>
    private async Task ProcessOrganizationalUnitsOptimizedAsync(
        List<Dictionary<string, string>> spreadsheetData,
        ImportConfig config,
        ImportAnalysis analysis,
        UserAnalysisCache cache,
        string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"Analyse optimisée des OUs depuis la colonne '{config.ouColumn}'");

        // Vérifier l'OU par défaut
        var defaultOuExists = await EnsureDefaultOuExistsAsync(config, analysis);

        if (string.IsNullOrEmpty(config.DefaultOU) || defaultOuExists)
        {
            var uniqueOuValues = ExtractUniqueOuValues(spreadsheetData, config);
            // 🆕 UTILISER la méthode centralisée au lieu de la version optimisée
            CreateOuActions(uniqueOuValues, cache.ExistingOUs, config, analysis);
        }
    }

    /// <summary>
    ///     Version optimisée des actions supplémentaires (avec vérifications asynchrones)
    /// </summary>
    private async Task<List<ImportAction>> ProcessAdditionalUserActionsOptimizedAsync(
        Dictionary<string, string> mappedRow,
        ImportConfig config,
        string cleanedSamAccountName,
        string ouPath)
    {
        _logger.LogInformation($"🔍 DÉBUT ProcessAdditionalUserActionsOptimizedAsync pour {cleanedSamAccountName}");
        
        var actions = new List<ImportAction>();

        // ✅ DÉBOGAGE AJOUTÉ : Log des vérifications d'actions supplémentaires
        _logger.LogDebug($"🔍 ProcessAdditionalUserActionsOptimized pour '{cleanedSamAccountName}'");
        _logger.LogDebug($"🔍 config.Folders = {(config.Folders != null ? "non-null" : "null")}");

        // ✅ CORRECTION : Enlever la vérification EnableShareProvisioning
        // La logique est : créer l'action si les paramètres sont présents, le filtrage se fait dans FilterDisabledActions
        if (config.Folders != null)
        {
            _logger.LogDebug($"🔍 Configuration Folders présente pour '{cleanedSamAccountName}' - appel de ProcessUserShareProvisioningOptimizedAsync");
            var shareAction = await ProcessUserShareProvisioningOptimizedAsync(mappedRow, config, cleanedSamAccountName);
            if (shareAction != null) 
            {
                actions.Add(shareAction);
                _logger.LogDebug($"✅ Action CREATE_STUDENT_FOLDER ajoutée pour '{cleanedSamAccountName}'");
            }
            else
            {
                _logger.LogWarning($"❌ ProcessUserShareProvisioningOptimizedAsync a retourné null pour '{cleanedSamAccountName}'");
            }
        }
        else
        {
            _logger.LogWarning($"❌ config.Folders est null pour '{cleanedSamAccountName}' - aucune action CREATE_STUDENT_FOLDER générée");
        }

        Console.WriteLine("ICI KERLANN2 OPTIMIZED");
        
        if (config.ClassGroupFolderCreationConfig != null)
        {
            var classGroupAction = ProcessClassGroupFolderCreationOptimized(mappedRow, config, cleanedSamAccountName);
            if (classGroupAction != null) actions.Add(classGroupAction);
        }

        // ✅ CORRECTION : Utiliser TeamsIntegration au lieu de TeamGroupCreationConfig
        if (config.TeamsIntegration != null)
        {
            Console.WriteLine("ICI KERLANN OPTIMIZED");
            _logger.LogInformation($"🔍 Configuration TeamsIntegration présente pour '{cleanedSamAccountName}' - vérification des conditions");
            var teamGroupAction = await ProcessTeamsIntegrationOptimizedAsync(mappedRow, config, cleanedSamAccountName);
            if (teamGroupAction != null) 
            {
                actions.Add(teamGroupAction);
                _logger.LogDebug($"✅ Action CREATE_TEAM ajoutée pour '{cleanedSamAccountName}'");
            }
        }
        else
        {
            _logger.LogInformation($"❌ config.TeamsIntegration est null pour {cleanedSamAccountName}");
            // Diagnostic détaillé
            _logger.LogInformation($"🔍 DIAGNOSTIC config pour {cleanedSamAccountName}:");
            _logger.LogInformation($"  - config != null: {config != null}");
            _logger.LogInformation($"  - config.TeamsIntegration != null: {config.TeamsIntegration != null}");
            if (config != null)
            {
                _logger.LogInformation($"  - config.DefaultOU: {config.DefaultOU}");
                _logger.LogInformation($"  - config.ouColumn: {config.ouColumn}");
                _logger.LogInformation($"  - config.Folders != null: {config.Folders != null}");
                _logger.LogInformation($"  - Type de config: {config.GetType().Name}");
                
                // Sérialiser la config pour voir son contenu JSON
                try
                {
                    var configJson = System.Text.Json.JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    _logger.LogInformation($"  - Configuration JSON complète: {configJson}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"  - Erreur lors de la sérialisation de config: {ex.Message}");
                }
            }
        }

        _logger.LogInformation($"🔍 FIN ProcessAdditionalUserActionsOptimizedAsync pour {cleanedSamAccountName} - {actions.Count} actions");
        return actions;
    }

    /// <summary>
    ///     Version optimisée du provisionnement de partage (avec vérification d'existence)
    /// </summary>
    private async Task<ImportAction?> ProcessUserShareProvisioningOptimizedAsync(
        Dictionary<string, string> mappedRow,
        ImportConfig config,
        string cleanedSamAccountName)
    {
        var folders = config.Folders;
        
        // ✅ DÉBOGAGE AJOUTÉ : Log de la configuration folders
        if (folders == null)
        {
            _logger.LogWarning($"❌ config.Folders est null pour l'utilisateur '{cleanedSamAccountName}' - Aucune action CREATE_STUDENT_FOLDER ne sera générée");
            return null;
        }
        
        // ✅ MODIFICATION : Log sans EnableShareProvisioning car ce n'est plus utilisé
        _logger.LogDebug($"🔍 ProcessUserShareProvisioningOptimized pour '{cleanedSamAccountName}':");
        _logger.LogDebug($"    • TargetServerName: '{folders.TargetServerName}'");
        _logger.LogDebug($"    • LocalPathForUserShareOnServer: '{folders.LocalPathForUserShareOnServer}'");
        _logger.LogDebug($"    • ShareNameForUserFolders: '{folders.ShareNameForUserFolders}'");

        // ✅ CORRECTION MAJEURE : Lire NetBiosDomainName depuis ConfigService (settings.json)
        string? globalNetBiosDomainName = null;
        try
        {
            var appSettings = _configService.GetAllSettingsAsync().Result;
            globalNetBiosDomainName = appSettings.NetBiosDomainName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Erreur lors de la récupération de NetBiosDomainName depuis ConfigService pour '{cleanedSamAccountName}'");
        }
        
        _logger.LogDebug($"    • NetBiosDomainName (depuis settings.json): '{globalNetBiosDomainName}'");

        // Vérification des paramètres requis
        var missingParams = new List<string>();
        if (string.IsNullOrWhiteSpace(folders.TargetServerName)) missingParams.Add("TargetServerName");
        if (string.IsNullOrWhiteSpace(folders.LocalPathForUserShareOnServer)) missingParams.Add("LocalPathForUserShareOnServer");
        if (string.IsNullOrWhiteSpace(folders.ShareNameForUserFolders)) missingParams.Add("ShareNameForUserFolders");
        if (string.IsNullOrWhiteSpace(globalNetBiosDomainName)) missingParams.Add("NetBiosDomainName");

        if (missingParams.Any())
        {
            _logger.LogWarning($"❌ Paramètres manquants pour CREATE_STUDENT_FOLDER de '{cleanedSamAccountName}': {string.Join(", ", missingParams)} manquant(s)");
            return null;
        }

        // ✅ CORRECTION CRITIQUE : Ajouter la vérification d'existence comme dans la version standard
        try
        {
            var shareExists = await _folderManagementService.CheckUserShareExistsAsync(
                folders.TargetServerName,
                cleanedSamAccountName,
                folders.LocalPathForUserShareOnServer
            );

            if (shareExists)
            {
                _logger.LogInformation(
                    "⏭️ Partage utilisateur '{SamAccountName}$' existe déjà sur {Server} - action CREATE_STUDENT_FOLDER ignorée",
                    cleanedSamAccountName, folders.TargetServerName);
                return null;
            }

            _logger.LogInformation(
                "🚀 Partage utilisateur '{SamAccountName}$' n'existe pas sur {Server} - action CREATE_STUDENT_FOLDER nécessaire",
                cleanedSamAccountName, folders.TargetServerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "⚠️ Erreur lors de la vérification du partage pour {SamAccountName}, création de l'action par sécurité",
                cleanedSamAccountName);
        }

        // Construction du chemin du dossier utilisateur
        var userFolderPath = string.IsNullOrWhiteSpace(folders.LocalPathForUserShareOnServer)
            ? cleanedSamAccountName
            : Path.Combine(folders.LocalPathForUserShareOnServer, cleanedSamAccountName);

        var action = new ImportAction
        {
            ActionType = ActionType.CREATE_STUDENT_FOLDER,
            ObjectName = cleanedSamAccountName,
            Path = userFolderPath,
            Message = $"Création du dossier utilisateur pour '{cleanedSamAccountName}'",
            Attributes = new Dictionary<string, string>
            {
                ["ServerName"] = folders.TargetServerName!,
                ["LocalPathForUserShareOnServer"] = folders.LocalPathForUserShareOnServer!,
                ["ShareNameForUserFolders"] = folders.ShareNameForUserFolders!,
                ["NetBiosDomainName"] = globalNetBiosDomainName!, 
                ["UserName"] = cleanedSamAccountName,
                ["AccountAd"] = $"{globalNetBiosDomainName}\\{cleanedSamAccountName}",
                ["IndividualShareName"] = cleanedSamAccountName + "$",
                ["Subfolders"] = System.Text.Json.JsonSerializer.Serialize(folders.DefaultShareSubfolders ?? new List<string>())
            }
        };

        _logger.LogDebug($"✅ Action CREATE_STUDENT_FOLDER créée pour '{cleanedSamAccountName}' → '{userFolderPath}'");
        return action;
    }

    /// <summary>
    ///     Version optimisée de la création de dossier de classe
    /// </summary>
    private ImportAction? ProcessClassGroupFolderCreationOptimized(
        Dictionary<string, string> mappedRow,
        ImportConfig config,
        string cleanedSamAccountName)
    {
        var classConfig = config.ClassGroupFolderCreationConfig;

        var shouldCreateVal =
            mappedRow.GetValueOrDefault(classConfig.CreateClassGroupFolderColumnName ?? "CreateClassGroupFolder");
        if (!bool.TryParse(shouldCreateVal, out var shouldCreate) || !shouldCreate)
            return null;

        var classGroupId = mappedRow.GetValueOrDefault(classConfig.ClassGroupIdColumnName ?? "ClassGroupId");
        var classGroupName = mappedRow.GetValueOrDefault(classConfig.ClassGroupNameColumnName ?? "ClassGroupName");
        var templateName = mappedRow.GetValueOrDefault(
            classConfig.ClassGroupTemplateNameColumnName ?? "ClassGroupTemplateName", "DefaultClassGroupTemplate");

        if (string.IsNullOrWhiteSpace(classGroupId) || string.IsNullOrWhiteSpace(classGroupName))
            return null;

        var classGroupAttributes = new Dictionary<string, string>(mappedRow)
        {
            ["Id"] = classGroupId,
            ["Name"] = classGroupName,
            ["TemplateName"] = templateName
        };

        return new ImportAction
        {
            ActionType = ActionType.CREATE_CLASS_GROUP_FOLDER,
            ObjectName = classGroupName,
            Path = "",
            Message = $"Création du dossier pour le groupe de classes {classGroupName}",
            Attributes = classGroupAttributes
        };
    }

    /// <summary>
    ///     Version optimisée de la création d'équipes Teams basée sur TeamsIntegration
    /// </summary>
    private async Task<ImportAction?> ProcessTeamsIntegrationOptimizedAsync(
        Dictionary<string, string> mappedRow,
        ImportConfig config,
        string cleanedSamAccountName)
    {
        _logger.LogInformation($"🔍 DÉBUT ProcessTeamsIntegrationOptimizedAsync pour '{cleanedSamAccountName}'");
        
        var teamsConfig = config.TeamsIntegration;
        
        if (teamsConfig == null)
        {
            _logger.LogInformation($"❌ config.TeamsIntegration est null pour '{cleanedSamAccountName}'");
            return null;
        }

        _logger.LogInformation($"🔍 ProcessTeamsIntegrationOptimizedAsync pour '{cleanedSamAccountName}':");
        _logger.LogInformation($"           Enabled: {teamsConfig.Enabled}");
        _logger.LogInformation($"           AutoAddUsersToTeams: {teamsConfig.AutoAddUsersToTeams}");

        if (!teamsConfig.Enabled)
        {
            _logger.LogInformation($"❌ Configuration Teams non activée pour '{cleanedSamAccountName}' (Enabled: {teamsConfig.Enabled})");
            return null;
        }

        // ✅ NOUVEAU : Vérifier si CREATE_TEAM est désactivé dans disabledActionTypes
        if (config.DisabledActionTypes != null && config.DisabledActionTypes.Contains(ActionType.CREATE_TEAM))
        {
            _logger.LogInformation($"⏭️ Action CREATE_TEAM désactivée pour '{cleanedSamAccountName}' via disabledActionTypes");
            return null;
        }

        _logger.LogInformation($"✅ Configuration Teams activée pour '{cleanedSamAccountName}'");

        // ✅ CORRECTION : Utiliser toujours 'division' pour Teams, pas config.ouColumn
        const string teamsClassColumn = "division"; // Colonne spécifique pour Teams
        
        // Extraction de la classe depuis la colonne 'division'
        var className = mappedRow.ContainsKey(teamsClassColumn) ? mappedRow[teamsClassColumn]?.Trim() : "";
        _logger.LogInformation($"🔍 Extraction classe depuis colonne '{teamsClassColumn}' → '{className}'");

        if (string.IsNullOrWhiteSpace(className))
        {
            _logger.LogInformation($"❌ Pas de classe/OU trouvée pour '{cleanedSamAccountName}' dans la colonne '{teamsClassColumn}'");
            return null;
        }

        _logger.LogInformation($"✅ Classe extraite: '{className}'");

        // Détermination du path OU basé sur config.ouColumn pour la structure AD
        var ouPath = DetermineUserOuPath(mappedRow, config);
        _logger.LogInformation($"🔍 OU Path déterminé: '{ouPath}'");

        if (string.IsNullOrWhiteSpace(ouPath))
        {
            _logger.LogInformation($"❌ OU Path invalide pour '{cleanedSamAccountName}'");
            return null;
        }

        _logger.LogInformation($"✅ OU Path valide: '{ouPath}'");

        // Génération du nom et de la description de l'équipe
        var teamName = teamsConfig.TeamNamingTemplate?.Replace("{OUName}", className) ?? $"Classe {className}";
        var teamDescription = $"Équipe collaborative pour la classe {className}";

        _logger.LogInformation($"🔍 Équipe Teams générée pour classe '{className}':");
        _logger.LogInformation($"           Nom: '{teamName}'");
        _logger.LogInformation($"           Description: '{teamDescription}'");
        _logger.LogInformation($"           OU Path: '{ouPath}'");
        _logger.LogInformation($"✅ DefaultTeacherUserId: '{teamsConfig.DefaultTeacherUserId}'");

        var action = new ImportAction
        {
            ActionType = ActionType.CREATE_TEAM,
            ObjectName = teamName,
            Path = ouPath,
            Message = $"Création de l'équipe Teams '{teamName}' pour la classe '{className}'",
            Attributes = new Dictionary<string, string>
            {
                ["TeamName"] = teamName,
                ["TeamDescription"] = teamDescription,
                ["ClassName"] = className,
                ["OUPath"] = ouPath,
                ["DefaultTeacherUserId"] = teamsConfig.DefaultTeacherUserId ?? "",
                ["AutoAddUsersToTeams"] = teamsConfig.AutoAddUsersToTeams.ToString(),
                ["ImportId"] = "optimized-analysis" // Identifiant pour l'analyse optimisée
            }
        };

        _logger.LogInformation($"✅ Action CREATE_TEAM créée pour équipe '{teamName}' (classe '{className}')");
        return action;
    }

    #endregion

    #endregion
}