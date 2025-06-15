using System.Collections.Concurrent;
using System.Text.Json;
using ADManagerAPI.Models;
using ADManagerAPI.Utils;

namespace ADManagerAPI.Services
{
    public partial class SpreadsheetImportService
    {
        #region Traitement des utilisateurs et actions supplémentaires

        private async Task ProcessUsersAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, ImportAnalysis analysis, string? connectionId = null, CancellationToken cancellationToken = default)
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
            var knownExistingOuPathsConcurrent = new ConcurrentHashSet<string>(knownExistingOuPathsCache, StringComparer.OrdinalIgnoreCase);

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
                        var actions = await ProcessUserRowParallelAsync(row, config, ousToBeCreated, knownExistingOuPathsConcurrent, groupsByOuCache);
                        foreach (var action in actions)
                        {
                            userActions.Add(action);
                        }

                        var completed = Interlocked.Increment(ref processedRows);
                        if (completed % 50 == 0 || completed == totalRows) // Mise à jour périodique
                        {
                            var progress = 70 + (completed * 15 / totalRows); // 70-85% pour les utilisateurs
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

        private async Task<List<ImportAction>> ProcessUserRowParallelAsync(Dictionary<string, string> row, ImportConfig config, HashSet<string> ousToBeCreated, ConcurrentHashSet<string> knownExistingOuPathsCache, Dictionary<string, List<string>> groupsByOuCache)
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
                    Message = "sAMAccountName manquant dans les données mappées pour une ligne.",
                    Attributes = row
                } };
            }
            
            string cleanedSamAccountName = samAccountName.Trim();
            _logger.LogInformation($"[SAM_DEBUG] sAMAccountName traité: '{cleanedSamAccountName}' pour la ligne CSV.");
            
            var actions = new List<ImportAction>();
            string ouPath = DetermineUserOuPath(row, config);
            bool ouExists = await DetermineOuExistenceParallel(ouPath, ousToBeCreated, knownExistingOuPathsCache, config, cleanedSamAccountName);
            
            if (!ouExists)
            {
                _logger.LogWarning($"L'OU '{ouPath}' n'existe pas et ne sera pas créée. Utilisation de l'OU par défaut pour l'utilisateur '{cleanedSamAccountName}'");
                ouPath = config.DefaultOU;
            }
            
            var userExists = await _ldapService.UserExistsAsync(cleanedSamAccountName);
            
            // ✅ AMÉLIORATION : Analyse intelligente pour UPDATE
            ActionType userActionType = ActionType.ERROR;
            string userActionMessage = null;
            bool shouldAddAction = true;
            
            if (userExists)
            {
                // 🔍 Pour les utilisateurs existants, vérifier si un déplacement ou une mise à jour est nécessaire
                _logger.LogInformation("🔍 Analyse des modifications nécessaires pour l'utilisateur existant {SamAccountName}...", cleanedSamAccountName);
                
                try
                {
                    // ✨ NOUVELLE FONCTIONNALITÉ : Vérifier si l'utilisateur doit être déplacé
                    var currentOu = await _ldapService.GetUserCurrentOuAsync(cleanedSamAccountName);
                    
                    _logger.LogInformation("[MOVE_USER_DEBUG] Utilisateur {SamAccountName} - OU actuelle: '{CurrentOu}', OU cible: '{TargetOu}'", 
                        cleanedSamAccountName, currentOu ?? "NULL", ouPath);
                    
                    if (!string.IsNullOrEmpty(currentOu) && !string.Equals(currentOu, ouPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // L'utilisateur est dans une OU différente de celle spécifiée dans le CSV
                        userActionType = ActionType.MOVE_USER;
                        userActionMessage = $"Déplacement nécessaire : utilisateur actuellement dans '{currentOu}', doit être dans '{ouPath}'";
                        _logger.LogInformation("🚚 Déplacement nécessaire pour {SamAccountName} : {CurrentOu} → {TargetOu}", cleanedSamAccountName, currentOu, ouPath);
                        
                        // Ajouter l'OU source dans les attributs pour l'exécution
                        mappedRow["SourceOU"] = currentOu;
                    }
                    else if (string.IsNullOrEmpty(currentOu))
                    {
                        _logger.LogWarning("[MOVE_USER_DEBUG] Impossible de déterminer l'OU actuelle pour {SamAccountName}, vérification des attributs", cleanedSamAccountName);
                        // Procéder avec la vérification des attributs
                        var attributesForComparison = PrepareAttributesForComparison(mappedRow);
                        var existingAttributes = await _ldapService.GetUserAttributesAsync(cleanedSamAccountName, attributesForComparison.Keys.ToList());
                        
                        if (existingAttributes.Any())
                        {
                            var hasChanges = HasAttributeChanges(attributesForComparison, existingAttributes, cleanedSamAccountName);
                            
                            if (hasChanges)
                            {
                                userActionType = ActionType.UPDATE_USER;
                                userActionMessage = "Mise à jour nécessaire : différences détectées dans les attributs";
                                _logger.LogInformation("✅ Mise à jour nécessaire pour {SamAccountName}", cleanedSamAccountName);
                            }
                            else
                            {
                                // ⏭️ Aucune modification nécessaire
                                _logger.LogInformation("⏭️ Aucune modification détectée pour {SamAccountName} - action ignorée", cleanedSamAccountName);
                                shouldAddAction = false;
                            }
                        }
                        else
                        {
                            userActionType = ActionType.UPDATE_USER;
                            userActionMessage = "Mise à jour prévue (impossible de comparer les attributs existants)";
                            _logger.LogWarning("⚠️ Impossible de comparer les attributs pour {SamAccountName}, mise à jour prévue par sécurité", cleanedSamAccountName);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("[MOVE_USER_DEBUG] Utilisateur {SamAccountName} déjà dans la bonne OU, vérification des attributs", cleanedSamAccountName);
                        // L'utilisateur est dans la bonne OU, vérifier les attributs
                        var attributesForComparison = PrepareAttributesForComparison(mappedRow);
                        var existingAttributes = await _ldapService.GetUserAttributesAsync(cleanedSamAccountName, attributesForComparison.Keys.ToList());
                        
                        if (existingAttributes.Any())
                        {
                            var hasChanges = HasAttributeChanges(attributesForComparison, existingAttributes, cleanedSamAccountName);
                            
                            if (hasChanges)
                            {
                                userActionType = ActionType.UPDATE_USER;
                                userActionMessage = "Mise à jour nécessaire : différences détectées dans les attributs";
                                _logger.LogInformation("✅ Mise à jour nécessaire pour {SamAccountName}", cleanedSamAccountName);
                            }
                            else
                            {
                                // ⏭️ Aucune modification nécessaire
                                _logger.LogInformation("⏭️ Aucune modification détectée pour {SamAccountName} - action ignorée", cleanedSamAccountName);
                                shouldAddAction = false;
                            }
                        }
                        else
                        {
                            userActionType = ActionType.UPDATE_USER;
                            userActionMessage = "Mise à jour prévue (impossible de comparer les attributs existants)";
                            _logger.LogWarning("⚠️ Impossible de comparer les attributs pour {SamAccountName}, mise à jour prévue par sécurité", cleanedSamAccountName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Erreur lors de l'analyse de {SamAccountName}, mise à jour prévue par sécurité", cleanedSamAccountName);
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
                    string displayName = userAttributes.ContainsKey("displayName") ? userAttributes["displayName"] : cleanedSamAccountName;
                    string userDn = $"CN={displayName},{ouPath}";
                    foreach (var groupName in groupNames)
                    {
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
            }

            // Traitement des actions supplémentaires
            var additionalActions = await ProcessAdditionalUserActionsParallel(mappedRow, config, cleanedSamAccountName, ouPath);
            actions.AddRange(additionalActions);

            return actions;
        }

        private string DetermineUserOuPath(Dictionary<string, string> row, ImportConfig config)
        {
            if (string.IsNullOrEmpty(config.ouColumn) || !row.ContainsKey(config.ouColumn) || string.IsNullOrEmpty(row[config.ouColumn]))
            {
                _logger.LogDebug("[MOVE_USER_DEBUG] Retour de l'OU par défaut - ouColumn: '{OuColumn}', contient la clé: {ContainsKey}, valeur: '{Value}'", 
                    config.ouColumn, row.ContainsKey(config.ouColumn ?? ""), row.GetValueOrDefault(config.ouColumn ?? ""));
                return config.DefaultOU;
            }
            
            string ouValue = row[config.ouColumn];
            string resultPath = BuildOuPath(ouValue, config.DefaultOU);
            
            _logger.LogDebug("[MOVE_USER_DEBUG] DetermineUserOuPath - ouColumn: '{OuColumn}', ouValue: '{OuValue}', resultPath: '{ResultPath}'", 
                config.ouColumn, ouValue, resultPath);
                
            return resultPath;
        }

        private async Task<List<string>> ProcessOrphanedUsersAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, ImportAnalysis analysis, CancellationToken cancellationToken = default)
        {
            string rootOuForCleanup = config.DefaultOU;
            List<string> finalOusToScan = new List<string>();

            if (string.IsNullOrEmpty(rootOuForCleanup))
            {
                analysis.Actions.Add(new ImportAction { ActionType = ActionType.ERROR, ObjectName = "Config Nettoyage Orphelins", Message = "config.DefaultOU non configurée." });
                return finalOusToScan;
            }

            var allSamAccountNamesFromSpreadsheet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rowInSpreadsheet in spreadsheetData)
            {
                var mappedAttributes = MapRow(rowInSpreadsheet, config);
                if (!mappedAttributes.TryGetValue("sAMAccountName", out var samAccountNameFromMapping) || string.IsNullOrEmpty(samAccountNameFromMapping))
                {
                    continue;
                }
                string cleanedSamForComparison = samAccountNameFromMapping.Split('(')[0].Trim();
                if (string.IsNullOrEmpty(cleanedSamForComparison))
                {
                    continue;
                }
                allSamAccountNamesFromSpreadsheet.Add(cleanedSamForComparison);
            }

            _logger.LogInformation($"[IMPORT ORPHANS] {allSamAccountNamesFromSpreadsheet.Count} sAMAccountNames collectés depuis le fichier.");

            try
            {
                var allUsersInAD = await _ldapService.GetAllUsersInOuAsync(rootOuForCleanup);
                _logger.LogInformation($"[IMPORT ORPHANS] {allUsersInAD.Count} utilisateurs trouvés dans l'AD sous '{rootOuForCleanup}'.");

                var orphanedUsers = allUsersInAD.Where(userAd => !allSamAccountNamesFromSpreadsheet.Contains(userAd.SamAccountName)).ToList();
                _logger.LogInformation($"[IMPORT ORPHANS] {orphanedUsers.Count} utilisateurs orphelins identifiés.");

                foreach (var orphanUser in orphanedUsers)
                {
                    analysis.Actions.Add(new ImportAction
                    {
                        ActionType = ActionType.DELETE_USER,
                        ObjectName = orphanUser.SamAccountName,
                        Path = orphanUser.DistinguishedName,
                        Message = $"Suppression de l'utilisateur orphelin '{orphanUser.SamAccountName}' (non présent dans le fichier d'import).",
                        Attributes = new Dictionary<string, string>
                        {
                            ["DistinguishedName"] = orphanUser.DistinguishedName,
                            ["DisplayName"] = orphanUser.DisplayName ?? ""
                        }
                    });
                }

                finalOusToScan = allUsersInAD.Select(u => ExtractOuFromDistinguishedName(u.DistinguishedName)).Distinct().ToList();
                _logger.LogInformation($"[IMPORT ORPHANS] {finalOusToScan.Count} OUs distinctes scannées pour le nettoyage.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IMPORT ORPHANS] Erreur lors du processus de nettoyage des orphelins.");
                analysis.Actions.Add(new ImportAction { ActionType = ActionType.ERROR, ObjectName = "Nettoyage Orphelins", Message = $"Erreur: {ex.Message}" });
            }

            return finalOusToScan;
        }

        /// <summary>
        /// ✅ NOUVELLE MÉTHODE : Prépare les attributs pour la comparaison en excluant les attributs système
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
            {
                if (!excludedAttributes.Contains(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    result[kvp.Key] = kvp.Value.Trim();
                }
            }
            
            return result;
        }

        /// <summary>
        /// ✅ NOUVELLE MÉTHODE : Vérifie s'il y a des changements entre les attributs nouveaux et existants
        /// </summary>
        private bool HasAttributeChanges(Dictionary<string, string> newAttributes, Dictionary<string, string?> existingAttributes, string samAccountName)
        {
            var changedAttributes = new List<string>();
            var unchangedAttributes = new List<string>();
            
            foreach (var newAttr in newAttributes)
            {
                var attributeName = newAttr.Key;
                var newValue = newAttr.Value?.Trim();
                var existingValue = existingAttributes.GetValueOrDefault(attributeName)?.Trim();
                
                // Normaliser les valeurs nulles/vides
                newValue = string.IsNullOrWhiteSpace(newValue) ? null : newValue;
                existingValue = string.IsNullOrWhiteSpace(existingValue) ? null : existingValue;
                
                // Comparer les valeurs
                bool isDifferent = !AreAttributeValuesEqualForComparison(attributeName, newValue, existingValue);
                
                if (isDifferent)
                {
                    changedAttributes.Add($"{attributeName}: '{existingValue}' → '{newValue}'");
                }
                else
                {
                    unchangedAttributes.Add($"{attributeName}: '{existingValue}'");
                }
            }
            
            // Logger les détails de la comparaison
            if (changedAttributes.Any())
            {
                _logger.LogInformation("🔄 Modifications détectées pour {SamAccountName}:", samAccountName);
                foreach (var change in changedAttributes)
                {
                    _logger.LogInformation("   📝 {Change}", change);
                }
            }
            
            if (unchangedAttributes.Any())
            {
                _logger.LogDebug("✅ Attributs inchangés pour {SamAccountName}: {UnchangedCount}", samAccountName, unchangedAttributes.Count);
                foreach (var unchanged in unchangedAttributes)
                {
                    _logger.LogDebug("   ✅ {Unchanged}", unchanged);
                }
            }
            
            return changedAttributes.Any();
        }

        /// <summary>
        /// ✅ MÉTHODE UTILITAIRE : Compare deux valeurs d'attributs pour l'analyse
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
            {
                return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Par défaut, comparaison insensible à la casse
                return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Version parallèle de la détermination de l'existence d'une OU
        /// </summary>
        private async Task<bool> DetermineOuExistenceParallel(string ouPath, HashSet<string> ousToBeCreated, ConcurrentHashSet<string> knownExistingOuPathsCache, ImportConfig config, string samAccountName)
        {
            if (string.IsNullOrEmpty(ouPath))
                return true;

            if (ousToBeCreated.Contains(ouPath))
            {
                _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' planifiée pour création. Utilisateur: '{samAccountName}'.");
                return true;
            }
            
            if (knownExistingOuPathsCache.Contains(ouPath))
            {
                _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' trouvée dans cache d'existence. Utilisateur: '{samAccountName}'.");
                return true;
            }
            
            _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' ni planifiée, ni en cache existance. Vérification AD. Utilisateur: '{samAccountName}'.");
            bool ouExists = await CheckOrganizationalUnitExistsAsync(ouPath);
            
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
                        _logger.LogInformation($"[USER_PROCESS] Ajout de l'action CREATE_OU pour l'OU manquante: '{ouPath}' requise par l'utilisateur '{samAccountName}'.");
                    }
                }
                return true;
            }
            
            return ouExists;
        }

        /// <summary>
        /// Version parallèle du traitement des actions supplémentaires pour un utilisateur
        /// </summary>
        private async Task<List<ImportAction>> ProcessAdditionalUserActionsParallel(Dictionary<string, string> mappedRow, ImportConfig config, string cleanedSamAccountName, string ouPath)
        {
            var actions = new List<ImportAction>();

            // Paralléliser les vérifications des actions supplémentaires
            var tasks = new List<Task<ImportAction?>>();

            if (config.Folders?.EnableShareProvisioning == true)
            {
                  tasks.Add(ProcessUserShareProvisioningParallel(mappedRow, config, cleanedSamAccountName));
            }

            if (config.ClassGroupFolderCreationConfig != null)
            {
                tasks.Add(ProcessClassGroupFolderCreationParallel(mappedRow, config, cleanedSamAccountName));
            }

            if (config.TeamGroupCreationConfig != null)
            {
                tasks.Add(ProcessTeamGroupCreationParallel(mappedRow, config, cleanedSamAccountName));
            }

            var results = await Task.WhenAll(tasks);
            actions.AddRange(results.Where(r => r != null).Cast<ImportAction>());

            return actions;
        }

        /// <summary>
        /// Version parallèle du provisionnement de partage utilisateur
        /// </summary>
        private async Task<ImportAction?> ProcessUserShareProvisioningParallel(Dictionary<string, string> mappedRow, ImportConfig config, string cleanedSamAccountName)
        {
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
                _logger.LogWarning($"Paramètres manquants pour le provisionnement du partage utilisateur {cleanedSamAccountName}: {string.Join(", ", missingParams)}");
                return null;
            }
            
            try
            {
                bool shareExists = await _folderManagementService.CheckUserShareExistsAsync(
                    folders.TargetServerName, 
                    cleanedSamAccountName, 
                    folders.LocalPathForUserShareOnServer
                );
                
                if (shareExists)
                {
                    _logger.LogInformation("⏭️ Partage utilisateur '{SamAccountName}$' existe déjà sur {Server} - action PROVISION_USER_SHARE ignorée", 
                        cleanedSamAccountName, folders.TargetServerName);
                    return null;
                }
                
                _logger.LogInformation("🚀 Partage utilisateur '{SamAccountName}$' n'existe pas sur {Server} - action PROVISION_USER_SHARE nécessaire", 
                    cleanedSamAccountName, folders.TargetServerName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Erreur lors de la vérification du partage pour {SamAccountName}, création de l'action par sécurité", cleanedSamAccountName);
            }
            
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
                Message = $"Préparation du provisionnement du partage utilisateur '{individualShareName}' pour '{accountAd}' sur '{folders.TargetServerName}'.",
                Attributes = shareAttributes
            };
        }

        /// <summary>
        /// Version parallèle de la création de dossier de groupe de classe
        /// </summary>
        private async Task<ImportAction?> ProcessClassGroupFolderCreationParallel(Dictionary<string, string> mappedRow, ImportConfig config, string cleanedSamAccountName)
        {
            var classConfig = config.ClassGroupFolderCreationConfig;
            
            string shouldCreateVal = mappedRow.GetValueOrDefault(classConfig.CreateClassGroupFolderColumnName ?? "CreateClassGroupFolder");
            
            if (!bool.TryParse(shouldCreateVal, out bool shouldCreate) || !shouldCreate)
                return null;
            
            string classGroupId = mappedRow.GetValueOrDefault(classConfig.ClassGroupIdColumnName ?? "ClassGroupId");
            string classGroupName = mappedRow.GetValueOrDefault(classConfig.ClassGroupNameColumnName ?? "ClassGroupName");
            string templateName = mappedRow.GetValueOrDefault(classConfig.ClassGroupTemplateNameColumnName ?? "ClassGroupTemplateName", "DefaultClassGroupTemplate");

            if (string.IsNullOrWhiteSpace(classGroupId) || string.IsNullOrWhiteSpace(classGroupName))
            {
                _logger.LogWarning($"Données manquantes pour CREATE_CLASS_GROUP_FOLDER pour {cleanedSamAccountName}: Id='{classGroupId}', Name='{classGroupName}'");
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
                Message = $"Préparation de la création du dossier pour le groupe de classes {classGroupName} (Modèle: {templateName})",
                Attributes = classGroupAttributes
            };
        }

        /// <summary>
        /// Version parallèle de la création de groupe Teams
        /// </summary>
        private async Task<ImportAction?> ProcessTeamGroupCreationParallel(Dictionary<string, string> mappedRow, ImportConfig config, string cleanedSamAccountName)
        {
            var teamConfig = config.TeamGroupCreationConfig;
            
            string shouldCreateVal = mappedRow.GetValueOrDefault(teamConfig.CreateTeamGroupColumnName ?? "CreateTeamGroup");
            
            if (!bool.TryParse(shouldCreateVal, out bool shouldCreate) || !shouldCreate)
                return null;
            
            string teamGroupName = mappedRow.GetValueOrDefault(teamConfig.TeamGroupNameColumnName ?? "TeamGroupName");

            if (string.IsNullOrWhiteSpace(teamGroupName))
            {
                _logger.LogWarning($"TeamGroupName manquant pour CREATE_TEAM_GROUP pour {cleanedSamAccountName}");
                return null;
            }

            var teamGroupAttributes = new Dictionary<string, string>(mappedRow)
            {
                ["Name"] = teamGroupName
            };

            return new ImportAction
            {
                ActionType = ActionType.CREATE_TEAM,
                ObjectName = teamGroupName,
                Path = "",
                Message = $"Préparation de la création du groupe Teams: {teamGroupName}",
                Attributes = teamGroupAttributes 
            };
        }

        #endregion
    }
}
