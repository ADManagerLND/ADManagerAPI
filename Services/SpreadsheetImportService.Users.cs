using System.Collections.Concurrent;
using System.Text.Json;
using ADManagerAPI.Models;
using ADManagerAPI.Utils;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ADManagerAPI.Services;

public partial class SpreadsheetImportService
{
    #region Traitement des utilisateurs et actions supplémentaires

    private async Task ProcessUsersAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config,
        ImportAnalysis analysis, string? connectionId = null, CancellationToken cancellationToken = default)
    {
        var ousToBeCreated = new HashSet<string>(
            analysis.Actions.Where(a => a.ActionType == ActionType.CREATE_OU).Select(a => a.Path),
            StringComparer.OrdinalIgnoreCase
        );
        var knownExistingOuPathsCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ✅ Cache des groupes par OU pour éviter les requêtes répétées
        var groupsByOuCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in analysis.Actions.Where(a => a.ActionType == ActionType.CREATE_GROUP))
        {
            if (!groupsByOuCache.ContainsKey(action.Path))
                groupsByOuCache[action.Path] = new List<string>();
            groupsByOuCache[action.Path].Add(action.ObjectName);
        }

        // Traitement en parallèle avec limitation de concurrence
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2); // Limiter la concurrence
        var userActions = new ConcurrentBag<ImportAction>();
        var totalRows = spreadsheetData.Count;
        var processedRows = 0;
        var knownExistingOuPathsConcurrent =
            new ConcurrentHashSet<string>(knownExistingOuPathsCache, StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(spreadsheetData,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken
            },
            async (row, ct) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var actions = await ProcessUserRowParallelAsync(row, config, ousToBeCreated,
                        knownExistingOuPathsConcurrent, groupsByOuCache);
                    foreach (var action in actions) userActions.Add(action);

                    var completed = Interlocked.Increment(ref processedRows);
                    if (completed % 50 == 0 || completed == totalRows) // Mise à jour périodique
                    {
                        var progress = 70 + completed * 15 / totalRows; // 70-85% pour les utilisateurs
                        await SendProgressUpdateAsync(connectionId, progress, "analyzing",
                            $"Traitement des utilisateurs... ({completed}/{totalRows})");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

        // Ajouter toutes les actions trouvées
        analysis.Actions.AddRange(userActions);
    }

    private async Task<List<ImportAction>> ProcessUserRowParallelAsync(Dictionary<string, string> row,
        ImportConfig config, HashSet<string> ousToBeCreated, ConcurrentHashSet<string> knownExistingOuPathsCache,
        Dictionary<string, List<string>> groupsByOuCache)
    {
        var mappedRow = MapRow(row, config);
        var samAccountName = mappedRow.GetValueOrDefault("sAMAccountName");

        if (string.IsNullOrEmpty(samAccountName))
            return new List<ImportAction>
            {
                new()
                {
                    ActionType = ActionType.ERROR,
                    ObjectName = "Unknown",
                    Path = config.DefaultOU,
                    Message = "sAMAccountName manquant dans les données mappées pour une ligne.",
                    Attributes = row
                }
            };

        var cleanedSamAccountName = samAccountName.Trim();
        _logger.LogInformation($"[SAM_DEBUG] sAMAccountName traité: '{cleanedSamAccountName}' pour la ligne CSV.");

        var actions = new List<ImportAction>();
        var ouPath = DetermineUserOuPath(row, config);
        var ouExists = await DetermineOuExistenceParallel(ouPath, ousToBeCreated, knownExistingOuPathsCache, config,
            cleanedSamAccountName);

        if (!ouExists)
        {
            _logger.LogWarning(
                $"L'OU '{ouPath}' n'existe pas et ne sera pas créée. Utilisation de l'OU par défaut pour l'utilisateur '{cleanedSamAccountName}'");
            ouPath = config.DefaultOU;
        }

        var userExists = await _ldapService.UserExistsAsync(cleanedSamAccountName);
        
        var userActionType = ActionType.ERROR;
        string userActionMessage = null;
        var shouldAddAction = true;

        if (userExists)
        {
            // 🔍 Pour les utilisateurs existants, vérifier si un déplacement ou une mise à jour est nécessaire
            _logger.LogInformation(
                "🔍 Analyse des modifications nécessaires pour l'utilisateur existant {SamAccountName}...",
                cleanedSamAccountName);

            try
            {
                var currentOu = await _ldapService.GetUserCurrentOuAsync(cleanedSamAccountName);

                _logger.LogInformation(
                    "[MOVE_USER_DEBUG] Utilisateur {SamAccountName} - OU actuelle: '{CurrentOu}', OU cible: '{TargetOu}'",
                    cleanedSamAccountName, currentOu ?? "NULL", ouPath);

                if (!string.IsNullOrEmpty(currentOu) &&
                    !string.Equals(currentOu, ouPath, StringComparison.OrdinalIgnoreCase))
                {
                    // L'utilisateur est dans une OU différente de celle spécifiée dans le CSV
                    userActionType = ActionType.MOVE_USER;
                    userActionMessage =
                        $"Déplacement nécessaire : utilisateur actuellement dans '{currentOu}', doit être dans '{ouPath}'";
                    _logger.LogInformation("🚚 Déplacement nécessaire pour {SamAccountName} : {CurrentOu} → {TargetOu}",
                        cleanedSamAccountName, currentOu, ouPath);

                    // Ajouter l'OU source dans les attributs pour l'exécution
                    mappedRow["SourceOU"] = currentOu;
                }
                else if (string.IsNullOrEmpty(currentOu))
                {
                    _logger.LogWarning(
                        "[MOVE_USER_DEBUG] Impossible de déterminer l'OU actuelle pour {SamAccountName}, vérification des attributs",
                        cleanedSamAccountName);
                    // Procéder avec la vérification des attributs
                    var attributesForComparison = PrepareAttributesForComparison(mappedRow);
                    var existingAttributes = await _ldapService.GetUserAttributesAsync(cleanedSamAccountName,
                        attributesForComparison.Keys.ToList());

                    if (existingAttributes.Any())
                    {
                        var hasChanges = HasAttributeChanges(attributesForComparison, existingAttributes,
                            cleanedSamAccountName);

                        if (hasChanges)
                        {
                            userActionType = ActionType.UPDATE_USER;
                            userActionMessage = "Mise à jour nécessaire : différences détectées dans les attributs";
                            _logger.LogInformation("✅ Mise à jour nécessaire pour {SamAccountName}",
                                cleanedSamAccountName);
                        }
                        else
                        {
                            // ⏭️ Aucune modification nécessaire
                            _logger.LogInformation(
                                "⏭️ Aucune modification détectée pour {SamAccountName} - action ignorée",
                                cleanedSamAccountName);
                            shouldAddAction = false;
                        }
                    }
                    else
                    {
                        userActionType = ActionType.UPDATE_USER;
                        userActionMessage = "Mise à jour prévue (impossible de comparer les attributs existants)";
                        _logger.LogWarning(
                            "⚠️ Impossible de comparer les attributs pour {SamAccountName}, mise à jour prévue par sécurité",
                            cleanedSamAccountName);
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "[MOVE_USER_DEBUG] Utilisateur {SamAccountName} déjà dans la bonne OU, vérification des attributs",
                        cleanedSamAccountName);
                    // L'utilisateur est dans la bonne OU, vérifier les attributs
                    var attributesForComparison = PrepareAttributesForComparison(mappedRow);
                    var existingAttributes = await _ldapService.GetUserAttributesAsync(cleanedSamAccountName,
                        attributesForComparison.Keys.ToList());

                    if (existingAttributes.Any())
                    {
                        var hasChanges = HasAttributeChanges(attributesForComparison, existingAttributes,
                            cleanedSamAccountName);

                        if (hasChanges)
                        {
                            userActionType = ActionType.UPDATE_USER;
                            userActionMessage = "Mise à jour nécessaire : différences détectées dans les attributs";
                            _logger.LogInformation("✅ Mise à jour nécessaire pour {SamAccountName}",
                                cleanedSamAccountName);
                        }
                        else
                        {
                            // ⏭️ Aucune modification nécessaire
                            _logger.LogInformation(
                                "⏭️ Aucune modification détectée pour {SamAccountName} - action ignorée",
                                cleanedSamAccountName);
                            shouldAddAction = false;
                        }
                    }
                    else
                    {
                        userActionType = ActionType.UPDATE_USER;
                        userActionMessage = "Mise à jour prévue (impossible de comparer les attributs existants)";
                        _logger.LogWarning(
                            "⚠️ Impossible de comparer les attributs pour {SamAccountName}, mise à jour prévue par sécurité",
                            cleanedSamAccountName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de l'analyse de {SamAccountName}, mise à jour prévue par sécurité",
                    cleanedSamAccountName);
                userActionType = ActionType.UPDATE_USER;
                userActionMessage = $"Mise à jour prévue (erreur lors de l'analyse: {ex.Message})";
            }
        }
        else
        {
            // Nouvel utilisateur
            userActionType = ActionType.CREATE_USER;
            userActionMessage = "Création d'un nouvel utilisateur";
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
            _logger.LogInformation($"✅ Action {userActionType} ajoutée pour {cleanedSamAccountName} dans {ouPath}");

            // Ajout automatique des actions ADD_USER_TO_GROUP pour tous les groupes connus de l'OU (cache)
            if (userActionType == ActionType.CREATE_USER && groupsByOuCache.TryGetValue(ouPath, out var groupNames))
            {
                // Construction du DN utilisateur (même logique que pour la création)
                var displayName = userAttributes.ContainsKey("displayName")
                    ? userAttributes["displayName"]
                    : cleanedSamAccountName;
                var userDn = $"CN={displayName},{ouPath}";
                foreach (var groupName in groupNames)
                    actions.Add(new ImportAction
                    {
                        ActionType = ActionType.ADD_USER_TO_GROUP,
                        ObjectName = cleanedSamAccountName,
                        Path = ouPath,
                        Message = $"Ajout de l'utilisateur '{cleanedSamAccountName}' au groupe '{groupName}'",
                        Attributes = new Dictionary<string, string>
                        {
                            { "userDn", userDn },
                            { "groupName", groupName },
                            { "ouDn", ouPath }
                        }
                    });
            }
        }
        Console.WriteLine("test");

        // Traitement des actions supplémentaires
        var additionalActions =
            await ProcessAdditionalUserActionsParallel(mappedRow, config, cleanedSamAccountName, ouPath);
        actions.AddRange(additionalActions);

        return actions;
    }

    private string DetermineUserOuPath(Dictionary<string, string> row, ImportConfig config)
    {
        if (string.IsNullOrEmpty(config.ouColumn) || !row.ContainsKey(config.ouColumn) ||
            string.IsNullOrEmpty(row[config.ouColumn]))
        {
            _logger.LogDebug(
                "[MOVE_USER_DEBUG] Retour de l'OU par défaut - ouColumn: '{OuColumn}', contient la clé: {ContainsKey}, valeur: '{Value}'",
                config.ouColumn, row.ContainsKey(config.ouColumn ?? ""), row.GetValueOrDefault(config.ouColumn ?? ""));
            return config.DefaultOU;
        }

        var ouValue = row[config.ouColumn];
        var resultPath = BuildOuPath(ouValue, config.DefaultOU);

        _logger.LogDebug(
            "[MOVE_USER_DEBUG] DetermineUserOuPath - ouColumn: '{OuColumn}', ouValue: '{OuValue}', resultPath: '{ResultPath}'",
            config.ouColumn, ouValue, resultPath);

        return resultPath;
    }

    private async Task<List<string>> ProcessOrphanedUsersAsync(List<Dictionary<string, string>> spreadsheetData,
        ImportConfig config, ImportAnalysis analysis, CancellationToken cancellationToken = default)
    {
        // ✅ CORRECTION : Toujours détecter les orphelins, le filtrage se fait plus tard
        // Note: IsOrphanCleanupEnabled retourne maintenant toujours true pour permettre la détection
        if (!IsOrphanCleanupEnabled(config))
        {
            _logger.LogWarning("⚠️ IsOrphanCleanupEnabled a retourné false - ceci ne devrait pas arriver");
            return new List<string>();
        }

        var rootOuForCleanup = config.DefaultOU;
        List<string> finalOusToScan = new List<string>();

        if (string.IsNullOrEmpty(rootOuForCleanup))
        {
            analysis.Actions.Add(new ImportAction
            {
                ActionType = ActionType.ERROR, ObjectName = "Config Nettoyage Orphelins",
                Message = "config.DefaultOU non configurée."
            });
            return finalOusToScan;
        }

        var allSamAccountNamesFromSpreadsheet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rowInSpreadsheet in spreadsheetData)
        {
            var mappedAttributes = MapRow(rowInSpreadsheet, config);
            if (!mappedAttributes.TryGetValue("sAMAccountName", out var samAccountNameFromMapping) ||
                string.IsNullOrEmpty(samAccountNameFromMapping)) continue;
            var cleanedSamForComparison = samAccountNameFromMapping.Split('(')[0].Trim();
            if (string.IsNullOrEmpty(cleanedSamForComparison)) continue;
            allSamAccountNamesFromSpreadsheet.Add(cleanedSamForComparison);
        }

        _logger.LogInformation(
            $"[IMPORT ORPHANS] {allSamAccountNamesFromSpreadsheet.Count} sAMAccountNames collectés depuis le fichier.");
        
     
            _logger.LogDebug($"📝 Utilisateurs extraits du fichier (legacy): {string.Join(", ", allSamAccountNamesFromSpreadsheet.Take(10))}");
            if (allSamAccountNamesFromSpreadsheet.Count > 10)
                _logger.LogDebug($"... et {allSamAccountNamesFromSpreadsheet.Count - 10} autres utilisateurs");
        
        try
        {
            var allUsersInAD = await _ldapService.GetAllUsersInOuAsync(rootOuForCleanup);
            _logger.LogInformation(
                $"[IMPORT ORPHANS] {allUsersInAD.Count} utilisateurs trouvés dans l'AD sous '{rootOuForCleanup}'.");

            var orphanedUsers = allUsersInAD
                .Where(userAd => !allSamAccountNamesFromSpreadsheet.Contains(userAd.SamAccountName)).ToList();
            _logger.LogInformation($"[IMPORT ORPHANS] {orphanedUsers.Count} utilisateurs orphelins identifiés.");
            
            // ✅ Log de debug pour voir les utilisateurs orphelins détectés (version legacy)
            if (orphanedUsers.Any() && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug($"🗑️ Utilisateurs orphelins détectés (legacy): {string.Join(", ", orphanedUsers.Take(10).Select(u => u.SamAccountName))}");
                if (orphanedUsers.Count > 10)
                    _logger.LogDebug($"... et {orphanedUsers.Count - 10} autres utilisateurs orphelins");
            }

            foreach (var orphanUser in orphanedUsers)
                analysis.Actions.Add(new ImportAction
                {
                    ActionType = ActionType.DELETE_USER,
                    ObjectName = orphanUser.SamAccountName,
                    Path = orphanUser.DistinguishedName,
                    Message =
                        $"Suppression de l'utilisateur orphelin '{orphanUser.SamAccountName}' (non présent dans le fichier d'import).",
                    Attributes = new Dictionary<string, string>
                    {
                        ["DistinguishedName"] = orphanUser.DistinguishedName,
                        ["DisplayName"] = orphanUser.DisplayName ?? ""
                    }
                });

            finalOusToScan = allUsersInAD.Select(u => ExtractOuFromDistinguishedName(u.DistinguishedName)).Distinct()
                .ToList();
            _logger.LogInformation(
                $"[IMPORT ORPHANS] {finalOusToScan.Count} OUs distinctes scannées pour le nettoyage.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IMPORT ORPHANS] Erreur lors du processus de nettoyage des orphelins.");
            analysis.Actions.Add(new ImportAction
            {
                ActionType = ActionType.ERROR, ObjectName = "Nettoyage Orphelins", Message = $"Erreur: {ex.Message}"
            });
        }

        return finalOusToScan;
    }

    /// <summary>
    ///     ✅ NOUVELLE MÉTHODE : Prépare les attributs pour la comparaison en excluant les attributs système
    /// </summary>
    private Dictionary<string, string> PrepareAttributesForComparison(Dictionary<string, string> mappedRow)
    {
        // Attributs à exclure de la comparaison (système, calculés ou non modifiables)
        var excludedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "password", "userPassword", "unicodePwd",
            "objectClass", "objectGuid", "objectSid",
            "whenCreated", "whenChanged", "lastLogon",
            "distinguishedName", "cn", "sAMAccountName" // CN et sAMAccountName ne changent généralement pas
        };

        var result = new Dictionary<string, string>();

        foreach (var kvp in mappedRow)
            if (!excludedAttributes.Contains(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                result[kvp.Key.ToLowerInvariant()] = kvp.Value.Trim(); // ✅ Normaliser la clé en minuscules

        return result;
    }

    /// <summary>
    ///     Vérifie s'il y a des changements entre les attributs nouveaux et existants
    /// </summary>
    private bool HasAttributeChanges(Dictionary<string, string> newAttributes,
        Dictionary<string, string?> existingAttributes, string samAccountName)
    {
        var changedAttributes = new List<string>();
        var unchangedAttributes = new List<string>();



        foreach (var newAttr in newAttributes)
        {
            var attributeName = newAttr.Key;
            var newValue = newAttr.Value?.Trim();
            // ✅ Rechercher avec la clé normalisée (minuscules)
            var normalizedKey = attributeName.ToLowerInvariant();
            var existingValue = existingAttributes.GetValueOrDefault(normalizedKey)?.Trim();

            // Normaliser les valeurs nulles/vides
            newValue = string.IsNullOrWhiteSpace(newValue) ? null : newValue;
            existingValue = string.IsNullOrWhiteSpace(existingValue) ? null : existingValue;



            // Comparer les valeurs
            var isDifferent = !AreAttributeValuesEqualForComparison(attributeName, newValue, existingValue);

            if (isDifferent)
            {
                changedAttributes.Add($"{attributeName}: '{existingValue}' → '{newValue}'");
            }
            else
            {
                unchangedAttributes.Add($"{attributeName}: '{existingValue}'");
            }
        }



        return changedAttributes.Any();
    }

    /// <summary>
    ///     ✅ MÉTHODE UTILITAIRE : Compare deux valeurs d'attributs pour l'analyse
    /// </summary>
    private bool AreAttributeValuesEqualForComparison(string attributeName, string? value1, string? value2)
    {
        // Attributs insensibles à la casse
        var caseInsensitiveAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mail", "userPrincipalName", "displayName", "givenName", "sn",
            "department", "company", "title", "physicalDeliveryOfficeName"
        };

        // Si les deux sont null ou vides, ils sont égaux
        if (string.IsNullOrWhiteSpace(value1) && string.IsNullOrWhiteSpace(value2))
            return true;

        // Si un seul est null/vide, ils sont différents
        if (string.IsNullOrWhiteSpace(value1) || string.IsNullOrWhiteSpace(value2))
            return false;

        // Comparaison selon le type d'attribut
        if (caseInsensitiveAttributes.Contains(attributeName))
            return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);

        // Par défaut, comparaison insensible à la casse
        return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Version parallèle de la détermination de l'existence d'une OU
    /// </summary>
    private async Task<bool> DetermineOuExistenceParallel(string ouPath, HashSet<string> ousToBeCreated,
        ConcurrentHashSet<string> knownExistingOuPathsCache, ImportConfig config, string samAccountName)
    {
        if (string.IsNullOrEmpty(ouPath))
            return true;

        if (ousToBeCreated.Contains(ouPath))
        {
            _logger.LogDebug(
                $"[CACHE_OU_LOGIC] OU '{ouPath}' planifiée pour création. Utilisateur: '{samAccountName}'.");
            return true;
        }

        if (knownExistingOuPathsCache.Contains(ouPath))
        {
            _logger.LogDebug(
                $"[CACHE_OU_LOGIC] OU '{ouPath}' trouvée dans cache d'existence. Utilisateur: '{samAccountName}'.");
            return true;
        }

        _logger.LogDebug(
            $"[CACHE_OU_LOGIC] OU '{ouPath}' ni planifiée, ni en cache existance. Vérification AD. Utilisateur: '{samAccountName}'.");
        var ouExists = await CheckOrganizationalUnitExistsAsync(ouPath);

        if (ouExists)
        {
            knownExistingOuPathsCache.Add(ouPath);
            _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' existe dans l'AD (ajoutée au cache).");
        }

        if (!ouExists && config.CreateMissingOUs)
        {
            lock (ousToBeCreated)
            {
                if (!ousToBeCreated.Contains(ouPath))
                {
                    ousToBeCreated.Add(ouPath);
                    _logger.LogInformation(
                        $"[USER_PROCESS] Ajout de l'action CREATE_OU pour l'OU manquante: '{ouPath}' requise par l'utilisateur '{samAccountName}'.");
                }
            }

            return true;
        }

        return ouExists;
    }

    /// <summary>
    ///     Version parallèle du traitement des actions supplémentaires pour un utilisateur
    /// </summary>
    private async Task<List<ImportAction>> ProcessAdditionalUserActionsParallel(Dictionary<string, string> mappedRow,
        ImportConfig config, string cleanedSamAccountName, string ouPath)
    {
        _logger.LogInformation($"🔍 DÉBUT ProcessAdditionalUserActionsParallel pour {cleanedSamAccountName}");
        
        var actions = new List<ImportAction>();

        // Paralléliser les vérifications des actions supplémentaires
        var tasks = new List<Task<ImportAction?>>();

        _logger.LogInformation($"🔍 Vérification config.Folders?.EnableShareProvisioning pour {cleanedSamAccountName}: {config.Folders?.EnableShareProvisioning}");
        
        // ⚠️ TEMPORAIRE : Désactiver le provisionnement de partage pour isoler le problème Teams
        var enableShareProvisioningTemp = false; // config.Folders?.EnableShareProvisioning == true;
        _logger.LogInformation($"🔍 Provisionnement de partage TEMPORAIREMENT DÉSACTIVÉ pour diagnostic Teams");
        
        if (enableShareProvisioningTemp)
        {
            _logger.LogInformation($"🔍 Ajout de ProcessUserShareProvisioningParallel pour {cleanedSamAccountName}");
            tasks.Add(ProcessUserShareProvisioningParallel(mappedRow, config, cleanedSamAccountName));
        }
        
        Console.WriteLine("ICI KERLANN2");
        /*if (config.ClassGroupFolderCreationConfig != null)
            tasks.Add(ProcessClassGroupFolderCreationParallel(mappedRow, config, cleanedSamAccountName));*/

        if (config.TeamsIntegration != null)
        {
            Console.WriteLine("ICI KERLANN");
            _logger.LogDebug($"🚀 TeamsIntegration configurée pour {cleanedSamAccountName} - Enabled: {config.TeamsIntegration.Enabled}");
            tasks.Add(ProcessTeamsIntegrationParallel(mappedRow, config, cleanedSamAccountName));
        }
        else
        {
            _logger.LogDebug($"❌ config.TeamsIntegration est null pour {cleanedSamAccountName}");
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

        _logger.LogInformation($"🔍 AVANT Task.WhenAll pour {cleanedSamAccountName} - {tasks.Count} tâches");
        var results = await Task.WhenAll(tasks);
        _logger.LogInformation($"🔍 APRÈS Task.WhenAll pour {cleanedSamAccountName}");
        
        actions.AddRange(results.Where(r => r != null).Cast<ImportAction>());

        _logger.LogInformation($"🔍 FIN ProcessAdditionalUserActionsParallel pour {cleanedSamAccountName} - {actions.Count} actions");
        return actions;
    }

    /// <summary>
    ///     Version parallèle du provisionnement de partage utilisateur
    /// </summary>
    private async Task<ImportAction?> ProcessUserShareProvisioningParallel(Dictionary<string, string> mappedRow,
        ImportConfig config, string cleanedSamAccountName)
    {
        _logger.LogInformation($"🔍 DÉBUT ProcessUserShareProvisioningParallel pour {cleanedSamAccountName}");
        
        var folders = config.Folders;

        var requiredParams = new Dictionary<string, string>
        {
            ["TargetServerName"] = folders.TargetServerName,
            ["LocalPathForUserShareOnServer"] = folders.LocalPathForUserShareOnServer,
            ["ShareNameForUserFolders"] = folders.ShareNameForUserFolders,
            ["NetBiosDomainName"] = config.NetBiosDomainName
        };

        var missingParams = requiredParams.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToList();

        if (missingParams.Any())
        {
            _logger.LogWarning(
                $"Paramètres manquants pour le provisionnement du partage utilisateur {cleanedSamAccountName}: {string.Join(", ", missingParams)}");
            return null;
        }

        _logger.LogInformation($"🔍 AVANT CheckUserShareExistsAsync pour {cleanedSamAccountName}");
        
        try
        {
            var shareExists = await _folderManagementService.CheckUserShareExistsAsync(
                folders.TargetServerName,
                cleanedSamAccountName,
                folders.LocalPathForUserShareOnServer
            );

            _logger.LogInformation($"🔍 APRÈS CheckUserShareExistsAsync pour {cleanedSamAccountName} - shareExists: {shareExists}");

            if (shareExists)
            {
                _logger.LogInformation(
                    "⏭️ Partage utilisateur '{SamAccountName}$' existe déjà sur {Server} - action PROVISION_USER_SHARE ignorée",
                    cleanedSamAccountName, folders.TargetServerName);
                return null;
            }

            _logger.LogInformation(
                "🚀 Partage utilisateur '{SamAccountName}$' n'existe pas sur {Server} - action PROVISION_USER_SHARE nécessaire",
                cleanedSamAccountName, folders.TargetServerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "⚠️ ERREUR lors de la vérification du partage pour {SamAccountName}, création de l'action par sécurité",
                cleanedSamAccountName);
        }

        _logger.LogInformation($"🔍 CRÉATION de l'action pour {cleanedSamAccountName}");

        var accountAd = $"{config.NetBiosDomainName}\\{cleanedSamAccountName}";
        var individualShareName = cleanedSamAccountName + "$";

        var shareAttributes = new Dictionary<string, string>(mappedRow)
        {
            ["ServerName"] = folders.TargetServerName,
            ["LocalPathForUserShareOnServer"] = folders.LocalPathForUserShareOnServer,
            ["ShareNameForUserFolders"] = folders.ShareNameForUserFolders,
            ["AccountAd"] = accountAd,
            ["Subfolders"] = JsonSerializer.Serialize(folders.DefaultShareSubfolders ?? new List<string>()),
            ["IndividualShareName"] = individualShareName
        };

        _logger.LogInformation($"🔍 FIN ProcessUserShareProvisioningParallel pour {cleanedSamAccountName}");

        return new ImportAction
        {
            ActionType = ActionType.CREATE_STUDENT_FOLDER,
            ObjectName = individualShareName,
            Path = folders.LocalPathForUserShareOnServer,
            Message =
                $"Préparation du provisionnement du partage utilisateur '{individualShareName}' pour '{accountAd}' sur '{folders.TargetServerName}'.",
            Attributes = shareAttributes
        };
    }

    /// <summary>
    ///     Version parallèle de la création de dossier de groupe de classe
    /// </summary>
    private async Task<ImportAction?> ProcessClassGroupFolderCreationParallel(Dictionary<string, string> mappedRow,
        ImportConfig config, string cleanedSamAccountName)
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
        {
            _logger.LogWarning(
                $"Données manquantes pour CREATE_CLASS_GROUP_FOLDER pour {cleanedSamAccountName}: Id='{classGroupId}', Name='{classGroupName}'");
            return null;
        }

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
            Message =
                $"Préparation de la création du dossier pour le groupe de classes {classGroupName} (Modèle: {templateName})",
            Attributes = classGroupAttributes
        };
    }

    /// <summary>
    ///     Version parallèle de la création d'équipes Teams basée sur TeamsIntegration
    /// </summary>
    private async Task<ImportAction?> ProcessTeamsIntegrationParallel(Dictionary<string, string> mappedRow,
        ImportConfig config, string cleanedSamAccountName)
    {
        var teamsConfig = config.TeamsIntegration;
        
        if (teamsConfig == null)
        {
            _logger.LogDebug($"❌ config.TeamsIntegration est null pour '{cleanedSamAccountName}'");
            return null;
        }

        _logger.LogInformation($"🔍 ProcessTeamsIntegrationParallel pour '{cleanedSamAccountName}':");
        _logger.LogInformation($"    • Enabled: {teamsConfig.Enabled}");
        _logger.LogInformation($"    • AutoAddUsersToTeams: {teamsConfig.AutoAddUsersToTeams}");

        // ✅ CORRECTION : Vérifier d'abord si l'intégration Teams est activée dans la configuration d'import
        if (!teamsConfig.Enabled)
        {
            _logger.LogInformation($"⏭️ Intégration Teams désactivée pour '{cleanedSamAccountName}' - Enabled: {teamsConfig.Enabled}");
            return null;
        }

        // ✅ NOUVEAU : Vérifier si CREATE_TEAM est désactivé dans disabledActionTypes
        if (config.DisabledActionTypes != null && config.DisabledActionTypes.Contains(ActionType.CREATE_TEAM))
        {
            _logger.LogInformation($"⏭️ Action CREATE_TEAM désactivée pour '{cleanedSamAccountName}' via disabledActionTypes");
            return null;
        }

        // Extraire la classe/OU de l'utilisateur
        var classe = mappedRow.GetValueOrDefault(config.ouColumn ?? "classe");
        _logger.LogInformation($"🔍 Extraction classe pour '{cleanedSamAccountName}': colonne='{config.ouColumn}', valeur='{classe}'");
        if (string.IsNullOrWhiteSpace(classe))
        {
            _logger.LogInformation($"❌ Pas de classe/OU trouvée pour '{cleanedSamAccountName}' dans la colonne '{config.ouColumn}'");
            return null;
        }

        // Construire l'OU path complet
        var ouPath = DetermineUserOuPath(mappedRow, config);
        if (string.IsNullOrWhiteSpace(ouPath))
        {
            _logger.LogWarning($"❌ Impossible de déterminer l'OU path pour '{cleanedSamAccountName}' avec classe '{classe}'");
            return null;
        }

        // Générer le nom de l'équipe Teams en utilisant le template
        var teamName = teamsConfig.TeamNamingTemplate?.Replace("{OUName}", classe) ?? $"Classe {classe}";
        var teamDescription = $"Équipe collaborative pour la classe {classe}";

        _logger.LogInformation($"📋 Équipe Teams générée pour classe '{classe}':");
        _logger.LogInformation($"    • Nom: '{teamName}'");
        _logger.LogInformation($"    • Description: '{teamDescription}'");
        _logger.LogInformation($"    • OU Path: '{ouPath}'");

        // Vérifier l'enseignant par défaut
        if (string.IsNullOrWhiteSpace(teamsConfig.DefaultTeacherUserId))
        {
            _logger.LogWarning($"⚠️ DefaultTeacherUserId manquant dans la configuration Teams pour '{cleanedSamAccountName}'");
        }

        var action = new ImportAction
        {
            ActionType = ActionType.CREATE_TEAM,
            ObjectName = teamName,
            Path = ouPath,
            Message = $"Préparation de la création de l'équipe Teams '{teamName}' pour la classe '{classe}'",
            Attributes = new Dictionary<string, string>
            {
                ["TeamName"] = teamName,
                ["TeamDescription"] = teamDescription,
                ["ClassName"] = classe,
                ["OUPath"] = ouPath,
                ["DefaultTeacherUserId"] = teamsConfig.DefaultTeacherUserId ?? "",
                ["UserToAdd"] = cleanedSamAccountName,
                ["TeamNamingTemplate"] = teamsConfig.TeamNamingTemplate,
                ["TeamDescriptionTemplate"] = teamsConfig.TeamDescriptionTemplate,
                ["AutoAddUsersToTeams"] = teamsConfig.AutoAddUsersToTeams.ToString(),
                ["ImportId"] = "parallel-analysis" // Identifiant pour l'analyse parallèle
            }
        };

        _logger.LogInformation($"✅ Action CREATE_TEAM créée pour équipe '{teamName}' (classe '{classe}')");
        return action;
    }

    #endregion
}