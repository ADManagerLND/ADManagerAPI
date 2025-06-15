using System.Diagnostics;
using System.Text.Json;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Utilities;
using Microsoft.AspNetCore.SignalR;
using ADManagerAPI.Hubs;
using System.Collections.Concurrent;
using ADManagerAPI.Utils;

namespace ADManagerAPI.Services
{
    public partial class SpreadsheetImportService
    {
        #region Analyse du fichier (CSV/Excel)

        public async Task<AnalysisResult> AnalyzeSpreadsheetContentAsync(Stream fileStream, string fileName, ImportConfig config, string? connectionId = null, CancellationToken cancellationToken = default)
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
            await SendProgressUpdateAsync(connectionId, 5, "parsing", "Lecture du fichier...");

            try
            {
                config = ImportConfigHelpers.EnsureValidConfig(config, _logger);

                var parser = ChooseParser(fileName);
                
                if (parser == null)
                {
                    _logger.LogError("Aucun service d'analyse de feuille de calcul n'a pu être déterminé.");
                    return new AnalysisResult
                    {
                        Success = false,
                        ErrorMessage = "Aucun service d'analyse de feuille de calcul n'a pu être déterminé pour le type de fichier."
                    };
                }

                await SendProgressUpdateAsync(connectionId, 15, "parsing", "Parsing des données...");
                var spreadsheetData = await parser.ParseAsync(fileStream, fileName, config.CsvDelimiter, config.ManualColumns);

                if (spreadsheetData.Count == 0)
                {
                    _logger.LogError("Aucune donnée valide trouvée dans le fichier.");
                    return new AnalysisResult
                    {
                        Success = false,
                        ErrorMessage = "Aucune donnée valide n'a été trouvée dans le fichier."
                    };
                }

                await SendProgressUpdateAsync(connectionId, 25, "analyzing", "Analyse des données...");
                
                FileDataStore.SetCsvData(spreadsheetData);
                var analysisResult = await AnalyzeSpreadsheetDataAsync(spreadsheetData, config, connectionId, cancellationToken);
                
                if (analysisResult.Success && analysisResult.Analysis != null)
                {
                    // 🔧 CORRECTION : Utiliser uniquement la méthode avec connectionId
                    if (!string.IsNullOrEmpty(connectionId))
                    {
                        Utils.AnalysisDataStore.SetAnalysis(connectionId, analysisResult.Analysis);
                        _logger.LogInformation($"✅ Analyse stockée pour connectionId: {connectionId}. Actions: {analysisResult.Analysis.Actions?.Count ?? 0}");
                    }
                    else
                    {
                        _logger.LogWarning($"⚠️ ConnectionId manquant, stockage dans AnalysisDataStore ignoré");
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

        public async Task<AnalysisResult> AnalyzeSpreadsheetDataAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, string? connectionId = null, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("🚀 Analyse de données de tableur déjà chargées");
            await SendProgressUpdateAsync(connectionId, 30, "analyzing", "Préparation de l'analyse...");

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

                config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
                var headers = spreadsheetData.FirstOrDefault()?.Keys.ToList() ?? new List<string>();
                var previewData = spreadsheetData.Take(10).ToList();

                var result = new AnalysisResult
                {
                    Success = true,
                    CsvData = spreadsheetData,
                    CsvHeaders = headers,
                    PreviewData = previewData.Select(row => row as object).ToList(),
                    TableData = spreadsheetData,
                    Errors = new List<string>(),
                    IsValid = true,
                };

                await SendProgressUpdateAsync(connectionId, 40, "analyzing", "Analyse des actions...");
                var analysis = await AnalyzeSpreadsheetDataForActionsAsync(spreadsheetData, config, connectionId, cancellationToken);

                if (analysis != null)
                {
                    result.Analysis = analysis;
                    result.Summary = new
                    {
                        TotalRows = spreadsheetData.Count,
                        ActionsCount = analysis.Actions.Count,
                        CreateCount = analysis.Summary.CreateCount,
                        UpdateCount = analysis.Summary.UpdateCount,
                        ErrorCount = analysis.Summary.ErrorCount
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

        public async Task<ImportAnalysis> AnalyzeSpreadsheetDataForActionsAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, string? connectionId = null, CancellationToken cancellationToken = default)
        {
            // ✅ UTILISER LA VERSION OPTIMISÉE PAR DÉFAUT
            const bool useOptimizedVersion = true; // Paramètre pour basculer vers l'ancienne version si nécessaire
            
            if (useOptimizedVersion)
            {
                try
                {
                    _logger.LogInformation("🚀 Utilisation de la version optimisée de l'analyse ({Count} lignes)", spreadsheetData.Count);
                    return await AnalyzeSpreadsheetDataForActionsOptimizedAsync(spreadsheetData, config, connectionId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Erreur avec la version optimisée, fallback vers la version standard");
                    // Fallback vers la version standard en cas d'erreur
                }
            }
            
            // VERSION STANDARD (fallback ou si optimisée désactivée)
            _logger.LogInformation("📊 Utilisation de la version standard de l'analyse ({Count} lignes)", spreadsheetData.Count);
            return await AnalyzeSpreadsheetDataForActionsLegacyAsync(spreadsheetData, config, connectionId, cancellationToken);
        }

        /// <summary>
        /// VERSION STANDARD (ancienne) conservée pour compatibilité et fallback
        /// </summary>
        public async Task<ImportAnalysis> AnalyzeSpreadsheetDataForActionsLegacyAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, string? connectionId = null, CancellationToken cancellationToken = default)
        {
            config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
            var analysis = new ImportAnalysis
            {
                Summary = new ImportSummary { TotalObjects = spreadsheetData.Count },
                Actions = new List<ImportAction>()
            };
            List<string> scannedOusForOrphanCleanup = new List<string>();

            try
            {
                await SendProgressUpdateAsync(connectionId, 50, "analyzing", "Traitement des unités organisationnelles...");
                
                if (ShouldProcessOrganizationalUnits(config))
                {
                    await ProcessOrganizationalUnitsAsync(spreadsheetData, config, analysis, connectionId, cancellationToken);
                }

                await SendProgressUpdateAsync(connectionId, 70, "analyzing", "Traitement des utilisateurs...");
                await ProcessUsersAsync(spreadsheetData, config, analysis, connectionId, cancellationToken);
                
                if (_enableOrphanCleanup)
                {
                    await SendProgressUpdateAsync(connectionId, 85, "analyzing", "Nettoyage des utilisateurs orphelins...");
                    scannedOusForOrphanCleanup = await ProcessOrphanedUsersAsync(spreadsheetData, config, analysis, cancellationToken);
                    
                    await SendProgressUpdateAsync(connectionId, 95, "analyzing", "Nettoyage des OUs vides...");
                    await ProcessEmptyOrganizationalUnitsAsync(scannedOusForOrphanCleanup, config, analysis, cancellationToken);
                }

                UpdateAnalysisSummary(analysis);
                await SendProgressUpdateAsync(connectionId, 100, "completed", "Analyse terminée");
                
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
            analysis.Summary.CreateStudentFolderCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_STUDENT_FOLDER);
            analysis.Summary.CreateClassGroupFolderCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_CLASS_GROUP_FOLDER);
            analysis.Summary.CreateTeamGroupCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_TEAM);
            analysis.Summary.ProvisionUserShareCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_STUDENT_FOLDER);
        }

        #region Méthodes utilitaires pour SignalR

        /// <summary>
        /// Envoie une mise à jour de progression via SignalR
        /// </summary>
        private async Task SendProgressUpdateAsync(string? connectionId, int progress, string status, string message)
        {
            if (!string.IsNullOrEmpty(connectionId) && _hubContext != null)
            {
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
        }

        #endregion

        #region ✅ NOUVELLES MÉTHODES OPTIMISÉES POUR PERFORMANCE

        /// <summary>
        /// Cache de comparaison pour éviter les recalculs
        /// </summary>
        private static readonly ConcurrentDictionary<string, bool> _attributeComparisonCache = new();

        /// <summary>
        /// Version optimisée de AnalyzeSpreadsheetDataForActionsAsync avec pré-chargement
        /// </summary>
        public async Task<ImportAnalysis> AnalyzeSpreadsheetDataForActionsOptimizedAsync(
            List<Dictionary<string, string>> spreadsheetData, 
            ImportConfig config, 
            string? connectionId = null, 
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("🚀 Analyse optimisée de {Count} lignes démarrée", spreadsheetData.Count);
            
            config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
            
            var analysis = new ImportAnalysis
            {
                Summary = new ImportSummary { TotalObjects = spreadsheetData.Count },
                Actions = new List<ImportAction>()
            };

            try
            {
                // 1. ✅ PRÉ-CHARGEMENT de toutes les données LDAP nécessaires (CRITIQUE)
                await SendProgressUpdateAsync(connectionId, 30, "analyzing", "Pré-chargement des données LDAP...");
                var cache = await PreloadUserDataAsync(spreadsheetData, config, connectionId, cancellationToken);

                // 2. Traitement des OUs (si nécessaire)
                if (ShouldProcessOrganizationalUnits(config))
                {
                    await SendProgressUpdateAsync(connectionId, 45, "analyzing", "Traitement des unités organisationnelles...");
                    await ProcessOrganizationalUnitsOptimizedAsync(spreadsheetData, config, analysis, cache, connectionId, cancellationToken);
                }

                // 3. ✅ TRAITEMENT OPTIMISÉ des utilisateurs
                await SendProgressUpdateAsync(connectionId, 55, "analyzing", "Traitement des utilisateurs...");
                await ProcessUsersOptimizedAsync(spreadsheetData, config, analysis, cache, connectionId, cancellationToken);
                
                // 4. Nettoyage optimisé des orphelins
                if (_enableOrphanCleanup)
                {
                    await SendProgressUpdateAsync(connectionId, 85, "analyzing", "Nettoyage des utilisateurs orphelins...");
                    var scannedOus = await ProcessOrphanedUsersOptimizedAsync(spreadsheetData, config, analysis, cache, cancellationToken);
                    
                    await SendProgressUpdateAsync(connectionId, 95, "analyzing", "Nettoyage des OUs vides...");
                    await ProcessEmptyOrganizationalUnitsAsync(scannedOus, config, analysis, cancellationToken);
                }

                UpdateAnalysisSummary(analysis);
                stopwatch.Stop();
                
                _logger.LogInformation($"✅ Analyse optimisée terminée en {stopwatch.ElapsedMilliseconds} ms " +
                                     $"({analysis.Actions.Count} actions générées) - Gain: {Math.Round((double)stopwatch.ElapsedMilliseconds / spreadsheetData.Count, 2)}ms/ligne");
                
                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de l'analyse optimisée des données");
                throw;
            }
        }

        /// <summary>
        /// ✅ PRÉ-CHARGE toutes les données LDAP nécessaires en lot pour éviter les appels répétés
        /// </summary>
        private async Task<UserAnalysisCache> PreloadUserDataAsync(
            List<Dictionary<string, string>> spreadsheetData, 
            ImportConfig config,
            string? connectionId = null,
            CancellationToken cancellationToken = default)
        {
            var cache = new UserAnalysisCache();
            var sw = Stopwatch.StartNew();

            // 1. Extraire tous les sAMAccountNames uniques du fichier
            var allSamAccountNames = spreadsheetData
                .AsParallel()
                .Select(row => MapRow(row, config))
                .Where(mapped => mapped.ContainsKey("sAMAccountName") && !string.IsNullOrEmpty(mapped["sAMAccountName"]))
                .Select(mapped => mapped["sAMAccountName"].Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogInformation($"🔍 Extraction de {allSamAccountNames.Count} sAMAccountNames uniques depuis {spreadsheetData.Count} lignes");

            // 2. ✅ PRÉ-CHARGER tous les utilisateurs existants en une seule requête LDAP
            if (allSamAccountNames.Any())
            {
                await SendProgressUpdateAsync(connectionId, 32, "analyzing", 
                    $"Chargement des utilisateurs existants ({allSamAccountNames.Count})...");

                try
                {
                    var existingUsers = await _ldapService.GetUsersBatchAsync(allSamAccountNames);
                    cache.ExistingUsers = existingUsers.ToDictionary(
                        u => u.SamAccountName, 
                        u => u, 
                        StringComparer.OrdinalIgnoreCase);
                    
                    cache.Statistics.TotalUsersLoaded = cache.ExistingUsers.Count;
                    _logger.LogInformation($"📥 {cache.ExistingUsers.Count} utilisateurs existants chargés depuis LDAP en une seule requête");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Erreur lors du chargement batch des utilisateurs, fallback vers méthode standard");
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
                await SendProgressUpdateAsync(connectionId, 37, "analyzing", 
                    $"Vérification des OUs ({allOuPaths.Count})...");

                try
                {
                    var existingOus = await _ldapService.GetOrganizationalUnitsBatchAsync(allOuPaths);
                    cache.ExistingOUs = existingOus.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    
                    cache.Statistics.TotalOUsLoaded = cache.ExistingOUs.Count;
                    _logger.LogInformation($"📁 {cache.ExistingOUs.Count} OUs existantes trouvées sur {allOuPaths.Count} vérifiées en une seule requête");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Erreur lors du chargement batch des OUs, fallback vers méthode standard");
                    // Fallback vers la méthode actuelle
                    await PreloadOUsIndividuallyAsync(allOuPaths, cache);
                }
            }

            sw.Stop();
            cache.Statistics.LoadTime = sw.Elapsed;
            _logger.LogInformation($"⚡ Pré-chargement terminé en {sw.ElapsedMilliseconds} ms (gain estimé: {Math.Round((double)(allSamAccountNames.Count + allOuPaths.Count) * 50 / 1000, 1)}s)");
            
            return cache;
        }

        /// <summary>
        /// Fallback pour le chargement individuel des utilisateurs
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
                    if (user != null)
                    {
                        foundUsers.Add(user);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(userTasks);
            
            // Stocker les UserModel complets
            foreach (var user in foundUsers)
            {
                cache.ExistingUsers[user.SamAccountName] = user;
            }
            
            cache.Statistics.TotalUsersLoaded = cache.ExistingUsers.Count;
            semaphore.Dispose();
            _logger.LogInformation($"📥 Fallback: {cache.ExistingUsers.Count} utilisateurs chargés individuellement");
        }

        /// <summary>
        /// Fallback pour le chargement individuel des OUs
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
                    if (exists)
                    {
                        foundOUs.Add(ouPath);
                    }
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
        /// Version optimisée du traitement des utilisateurs SANS appels LDAP répétés
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

            // ✅ TRAITEMENT EN PARALLÈLE OPTIMISÉ (plus de threads car plus d'I/O bound)
            await Parallel.ForEachAsync(spreadsheetData, 
                new ParallelOptions 
                { 
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 4, 32), // Optimisé pour I/O
                    CancellationToken = cancellationToken
                },
                async (row, ct) =>
                {
                    var actions = ProcessUserRowOptimized(row, config, ousToBeCreated, cache);
                    
                    if (actions.Any())
                    {
                        foreach (var action in actions)
                        {
                            userActions.Add(action);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref skippedRows);
                    }

                    var completed = Interlocked.Increment(ref processedRows);
                    if (completed % 100 == 0 || completed == totalRows) // Mise à jour moins fréquente
                    {
                        var progress = 55 + (completed * 25 / totalRows); // 55-80% pour les utilisateurs
                        await SendProgressUpdateAsync(connectionId, progress, "analyzing", 
                            $"Traitement des utilisateurs... ({completed}/{totalRows})");
                    }
                });

            analysis.Actions.AddRange(userActions);
            
            _logger.LogInformation($"🚀 {userActions.Count} actions utilisateur générées pour {totalRows} lignes " +
                                 $"({skippedRows} lignes ignorées car aucune action nécessaire)");
        }

        /// <summary>
        /// ✅ VERSION OPTIMISÉE du traitement d'une ligne utilisateur (SANS appels LDAP)
        /// </summary>
        private List<ImportAction> ProcessUserRowOptimized(
            Dictionary<string, string> row, 
            ImportConfig config, 
            ConcurrentHashSet<string> ousToBeCreated, 
            UserAnalysisCache cache)
        {
            var mappedRow = MapRow(row, config);
            string? samAccountName = mappedRow.GetValueOrDefault("sAMAccountName");

            if (string.IsNullOrEmpty(samAccountName))
            {
                return new List<ImportAction> { new ImportAction
                {
                    ActionType = ActionType.ERROR,
                    ObjectName = "Unknown",
                    Path = config.DefaultOU,
                    Message = "sAMAccountName manquant dans les données mappées",
                    Attributes = row
                } };
            }
            
            string cleanedSamAccountName = samAccountName.Trim();
            var actions = new List<ImportAction>();
            string ouPath = DetermineUserOuPath(row, config);
            
            // ✅ VÉRIFICATION OU depuis le cache (SANS appel LDAP)
            bool ouExists = ousToBeCreated.Contains(ouPath) || cache.ExistingOUs.Contains(ouPath);
            
            if (!ouExists)
            {
                if (config.CreateMissingOUs)
                {
                    ousToBeCreated.Add(ouPath);
                    ouExists = true;
                }
                else
                {
                    _logger.LogWarning($"OU '{ouPath}' n'existe pas, utilisation de l'OU par défaut pour '{cleanedSamAccountName}'");
                    ouPath = config.DefaultOU;
                }
            }
            
          
            bool userExists = cache.ExistingUsers.ContainsKey(cleanedSamAccountName);
            
            ActionType userActionType = ActionType.ERROR;
            string userActionMessage = null;
            bool shouldAddAction = true;
            
            if (userExists)
            {
                // ✨ Vérifier si l'utilisateur doit être déplacé (version optimisée)
                var existingUser = cache.ExistingUsers[cleanedSamAccountName];
                var currentOu = existingUser.OrganizationalUnit;

                if (!string.IsNullOrEmpty(currentOu) && !string.Equals(currentOu, ouPath, StringComparison.OrdinalIgnoreCase))
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

            // Actions supplémentaires (sans appels LDAP externes)
            var additionalActions = ProcessAdditionalUserActionsOptimized(mappedRow, config, cleanedSamAccountName, ouPath);
            actions.AddRange(additionalActions);

            return actions;
        }

        /// <summary>
        /// ✅ COMPARAISON OPTIMISÉE des attributs avec cache intelligent
        /// </summary>
        private bool HasAttributeChangesOptimized(
            Dictionary<string, string> mappedRow, 
            string samAccountName, 
            UserAnalysisCache cache)
        {
            var attributesForComparison = PrepareAttributesForComparison(mappedRow);
            
            // Créer une clé de cache basée sur les attributs
            var attributeHash = string.Join("|", attributesForComparison.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
            var cacheKey = $"{samAccountName}:{attributeHash.GetHashCode()}";
            
            return _attributeComparisonCache.GetOrAdd(cacheKey, _ => 
            {
                // Si nous avons les attributs existants dans le cache, les utiliser
                if (cache.UserAttributes.TryGetValue(samAccountName, out var existingAttributes))
                {
                    return HasAttributeChanges(attributesForComparison, existingAttributes, samAccountName);
                }
                
                // Si nous avons l'utilisateur en cache, assumer qu'une mise à jour peut être nécessaire
                if (cache.ExistingUsers.ContainsKey(samAccountName))
                {
                    // Comparer avec les attributs de base depuis LdapUser
                    var ldapUser = cache.ExistingUsers[samAccountName];
                    var basicExistingAttributes = new Dictionary<string, string?>
                    {
                        ["displayName"] = ldapUser.DisplayName,
                        ["givenName"] = ldapUser.GivenName,
                        ["sn"] = ldapUser.Surname,
                        ["mail"] = ldapUser.AdditionalAttributes.GetValueOrDefault("mail"),
                        ["userPrincipalName"] = ldapUser.UserPrincipalName,
                        ["department"] = ldapUser.AdditionalAttributes.GetValueOrDefault("department"),
                        ["title"] = ldapUser.AdditionalAttributes.GetValueOrDefault("title"),
                        ["telephoneNumber"] = ldapUser.AdditionalAttributes.GetValueOrDefault("telephoneNumber")
                    };
                    
                    return HasAttributeChanges(attributesForComparison, basicExistingAttributes, samAccountName);
                }
                
                // Sinon, assumer qu'une mise à jour est nécessaire par sécurité
                _logger.LogDebug($"Pas d'attributs en cache pour {samAccountName}, mise à jour prévue par sécurité");
                return true;
            });
        }

        /// <summary>
        /// Version optimisée du nettoyage des orphelins
        /// </summary>
        private async Task<List<string>> ProcessOrphanedUsersOptimizedAsync(
            List<Dictionary<string, string>> spreadsheetData, 
            ImportConfig config, 
            ImportAnalysis analysis, 
            UserAnalysisCache cache,
            CancellationToken cancellationToken = default)
        {
            string rootOuForCleanup = config.DefaultOU;
            
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

            // ✅ UTILISER les données déjà chargées du fichier
            var spreadsheetUsers = spreadsheetData
                .AsParallel()
                .Select(row => MapRow(row, config))
                .Where(mapped => mapped.ContainsKey("sAMAccountName") && !string.IsNullOrEmpty(mapped["sAMAccountName"]))
                .Select(mapped => mapped["sAMAccountName"].Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation($"🔍 {spreadsheetUsers.Count} utilisateurs du fichier pour comparaison orphelins");

            try
            {
                // ✅ UTILISER une requête LDAP optimisée avec filtre
                var allUsersInAD = await _ldapService.GetAllUsersInOuAsync(rootOuForCleanup);
                _logger.LogInformation($"📋 {allUsersInAD.Count} utilisateurs trouvés dans l'AD sous '{rootOuForCleanup}'");

                // ✅ COMPARAISON OPTIMISÉE O(n) au lieu de O(n²)
                var orphanedUsers = allUsersInAD
                    .AsParallel()
                    .Where(userAd => !spreadsheetUsers.Contains(userAd.SamAccountName))
                    .ToList();
                
                _logger.LogInformation($"🗑️ {orphanedUsers.Count} utilisateurs orphelins identifiés");

                // Ajouter les actions de suppression
                var deleteActions = orphanedUsers.Select(orphanUser => new ImportAction
                {
                    ActionType = ActionType.DELETE_USER,
                    ObjectName = orphanUser.SamAccountName,
                    Path = orphanUser.OrganizationalUnit,
                    Message = $"Suppression de l'utilisateur orphelin '{orphanUser.SamAccountName}' (non présent dans le fichier)",
                    Attributes = new Dictionary<string, string>
                    {
                        ["DistinguishedName"] = orphanUser.AdditionalAttributes?.GetValueOrDefault("distinguishedName") ?? "",
                        ["DisplayName"] = orphanUser.DisplayName ?? ""
                    }
                }).ToList();

                analysis.Actions.AddRange(deleteActions);

                return allUsersInAD
                    .Select(u => u.OrganizationalUnit)
                    .Where(ou => !string.IsNullOrEmpty(ou))
                    .Distinct()
                    .ToList();
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
        /// Version optimisée du traitement des OUs avec cache - UTILISE LA MÉTHODE CENTRALISÉE
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
            bool defaultOuExists = await EnsureDefaultOuExistsAsync(config, analysis);

            if (string.IsNullOrEmpty(config.DefaultOU) || defaultOuExists)
            {
                var uniqueOuValues = ExtractUniqueOuValues(spreadsheetData, config);
                // 🆕 UTILISER la méthode centralisée au lieu de la version optimisée
                CreateOuActions(uniqueOuValues, cache.ExistingOUs, config, analysis);
            }
        }

        /// <summary>
        /// Version optimisée des actions supplémentaires (sans appels LDAP)
        /// </summary>
        private List<ImportAction> ProcessAdditionalUserActionsOptimized(
            Dictionary<string, string> mappedRow, 
            ImportConfig config, 
            string cleanedSamAccountName, 
            string ouPath)
        {
            var actions = new List<ImportAction>();

            // Traitement synchrone des actions supplémentaires (pas d'appels LDAP externes)
            if (config.Folders?.EnableShareProvisioning == true)
            {
                var shareAction = ProcessUserShareProvisioningOptimized(mappedRow, config, cleanedSamAccountName);
                if (shareAction != null) actions.Add(shareAction);
            }

            if (config.ClassGroupFolderCreationConfig != null)
            {
                var classGroupAction = ProcessClassGroupFolderCreationOptimized(mappedRow, config, cleanedSamAccountName);
                if (classGroupAction != null) actions.Add(classGroupAction);
            }

            if (config.TeamGroupCreationConfig != null)
            {
                var teamGroupAction = ProcessTeamGroupCreationOptimized(mappedRow, config, cleanedSamAccountName);
                if (teamGroupAction != null) actions.Add(teamGroupAction);
            }

            return actions;
        }

        /// <summary>
        /// Version optimisée du provisionnement de partage (sans vérification d'existence)
        /// </summary>
        private ImportAction? ProcessUserShareProvisioningOptimized(
            Dictionary<string, string> mappedRow, 
            ImportConfig config, 
            string cleanedSamAccountName)
        {
            var folders = config.Folders;
            
            // Validation rapide des paramètres
            if (string.IsNullOrWhiteSpace(folders.TargetServerName) ||
                string.IsNullOrWhiteSpace(folders.LocalPathForUserShareOnServer) ||
                string.IsNullOrWhiteSpace(folders.ShareNameForUserFolders) ||
                string.IsNullOrWhiteSpace(config.NetBiosDomainName))
            {
                return null;
            }

            // Note: On assume que le partage doit être créé, la vérification 
            // d'existence sera faite pendant l'exécution pour éviter les appels réseau ici
            
            string accountAd = $"{config.NetBiosDomainName}\\{cleanedSamAccountName}";
            string individualShareName = cleanedSamAccountName + "$";
            
            var shareAttributes = new Dictionary<string, string>(mappedRow)
            {
                ["ServerName"] = folders.TargetServerName,
                ["LocalPathForUserShareOnServer"] = folders.LocalPathForUserShareOnServer,
                ["ShareNameForUserFolders"] = folders.ShareNameForUserFolders,
                ["AccountAd"] = accountAd,
                ["Subfolders"] = JsonSerializer.Serialize(folders.DefaultShareSubfolders ?? new List<string>()),
                ["IndividualShareName"] = individualShareName
            };

            return new ImportAction
            {
                ActionType = ActionType.CREATE_STUDENT_FOLDER,
                ObjectName = individualShareName,
                Path = folders.LocalPathForUserShareOnServer,
                Message = $"Provisionnement du partage utilisateur '{individualShareName}'",
                Attributes = shareAttributes
            };
        }

        /// <summary>
        /// Version optimisée de la création de dossier de classe
        /// </summary>
        private ImportAction? ProcessClassGroupFolderCreationOptimized(
            Dictionary<string, string> mappedRow, 
            ImportConfig config, 
            string cleanedSamAccountName)
        {
            var classConfig = config.ClassGroupFolderCreationConfig;
            
            string shouldCreateVal = mappedRow.GetValueOrDefault(classConfig.CreateClassGroupFolderColumnName ?? "CreateClassGroupFolder");
            if (!bool.TryParse(shouldCreateVal, out bool shouldCreate) || !shouldCreate)
                return null;
            
            string classGroupId = mappedRow.GetValueOrDefault(classConfig.ClassGroupIdColumnName ?? "ClassGroupId");
            string classGroupName = mappedRow.GetValueOrDefault(classConfig.ClassGroupNameColumnName ?? "ClassGroupName");
            string templateName = mappedRow.GetValueOrDefault(classConfig.ClassGroupTemplateNameColumnName ?? "ClassGroupTemplateName", "DefaultClassGroupTemplate");

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
        /// Version optimisée de la création de groupe Teams
        /// </summary>
        private ImportAction? ProcessTeamGroupCreationOptimized(
            Dictionary<string, string> mappedRow, 
            ImportConfig config, 
            string cleanedSamAccountName)
        {
            var teamConfig = config.TeamGroupCreationConfig;
            
            string shouldCreateVal = mappedRow.GetValueOrDefault(teamConfig.CreateTeamGroupColumnName ?? "CreateTeamGroup");
            if (!bool.TryParse(shouldCreateVal, out bool shouldCreate) || !shouldCreate)
                return null;
            
            string teamGroupName = mappedRow.GetValueOrDefault(teamConfig.TeamGroupNameColumnName ?? "TeamGroupName");
            if (string.IsNullOrWhiteSpace(teamGroupName))
                return null;

            var teamGroupAttributes = new Dictionary<string, string>(mappedRow)
            {
                ["Name"] = teamGroupName
            };

            return new ImportAction
            {
                ActionType = ActionType.CREATE_TEAM,
                ObjectName = teamGroupName,
                Path = "",
                Message = $"Création du groupe Teams: {teamGroupName}",
                Attributes = teamGroupAttributes 
            };
        }

        #endregion

        #endregion
    }
}