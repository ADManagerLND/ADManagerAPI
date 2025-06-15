using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;

namespace ADManagerAPI.Services
{
    public partial class SpreadsheetImportService
    {
        #region Exécution des actions d'import

        public async Task<ImportResult> ExecuteImportFromAnalysisAsync(ImportAnalysis analysis, ImportConfig config, string? connectionId = null)
        {
            _logger.LogInformation($"Exécution de l'import à partir de l'analyse précédente.");
            
            if (analysis == null || analysis.Actions == null || !analysis.Actions.Any())
            {
                _logger.LogWarning("Aucune action à exécuter dans l'analyse fournie.");
                return new ImportResult { Success = false, Message = "Aucune action à exécuter." };
            }
            
            return await ExecuteImport(analysis.Actions, config, connectionId);
        }

        public async Task<ImportResult> ExecuteImportFromActionsAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, List<LegacyImportActionItem> actions, string? connectionId = null)
        {
            if (actions == null || !actions.Any())
            {
                _logger.LogWarning("Aucune action à exécuter.");
                return new ImportResult { Success = false, Message = "Aucune action à exécuter." };
            }

            var importActions = new List<ImportAction>();
            
            foreach (var item in actions)
            {
                if (!item.Selected) continue;
                
                if (!Enum.TryParse<ActionType>(item.ActionType, out var actionType))
                {
                    _logger.LogWarning($"Type d'action inconnu: {item.ActionType}");
                    continue;
                }
                
                var row = spreadsheetData.FirstOrDefault(r => r.ContainsKey("index") && r["index"] == item.RowIndex.ToString());
                if (row == null)
                {
                    _logger.LogWarning($"Ligne d'index {item.RowIndex} introuvable dans les données.");
                    continue;
                }
                
                var mappedRow = MapRow(row, config);
                string ouPath = item.OuPath;

                importActions.Add(new ImportAction
                {
                    ActionType = actionType,
                    ObjectName = item.ObjectName,
                    Path = ouPath,
                    Message = item.Message,
                    Attributes = mappedRow
                });
            }
            
            _logger.LogInformation($"Conversion de {actions.Count} actions legacy en {importActions.Count} actions d'import.");
            return await ExecuteImport(importActions, config, connectionId);
        }

        public async Task<ImportResult> ExecuteImport(List<ImportAction> actions, ImportConfig config, string? connectionId = null)
        {
            _logger.LogInformation($"Démarrage de l'exécution de l'import avec {actions.Count} actions");
            var result = new ImportResult
            {
                Success = true,
                TotalActions = actions.Count,
                Results = new List<ImportActionResult>()
            };

            // Trier les actions pour garantir que la création des OUs précède les autres actions
            var sortedActions = SortActionsByPriority(actions);
            int processedCount = 0;

            using var scope = _serviceScopeFactory.CreateScope();
            var signalRService = scope.ServiceProvider.GetRequiredService<ISignalRService>();

            await signalRService.SendImportStartedAsync(connectionId, sortedActions.Count);
            string currentPhase = "Initialisation de l'import";
            
            // 🚀 OPTIMISATION: Grouper les actions PROVISION_USER_SHARE pour traitement batch
            var batchProvisionActions = sortedActions.Where(a => a.ActionType == ActionType.CREATE_STUDENT_FOLDER).ToList();
            var otherActions = sortedActions.Where(a => a.ActionType != ActionType.CREATE_STUDENT_FOLDER).ToList();
            
            // Traiter d'abord les autres actions individuellement
            foreach (var action in otherActions)
            {
                try
                {
                    processedCount++;
                    currentPhase = GetPhaseNameForAction(action.ActionType);
                    await signalRService.SendImportProgressAsync(connectionId, processedCount, sortedActions.Count, $"{currentPhase} - {action.ObjectName}");
                    
                    var actionResult = await ExecuteImportActionAsync(action, result, config);
                    result.Results.Add(actionResult);
                    
                    UpdateCountsAndSendProgress(result, actionResult, action, processedCount, sortedActions.Count, signalRService, connectionId, currentPhase);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Erreur lors de l'exécution de l'action {action.ActionType} pour {action.ObjectName}: {ex.Message}");
                    var failedActionResult = new ImportActionResult
                    {
                        Success = false,
                        ActionType = action.ActionType,
                        ObjectName = action.ObjectName,
                        Path = action.Path,
                        Message = $"Erreur: {ex.Message}",
                        Exception = ex
                    };
                    
                    result.Results.Add(failedActionResult);
                    result.ErrorCount++;
                }
            }
            
            // 🚀 Traitement BATCH des actions PROVISION_USER_SHARE
            if (batchProvisionActions.Any())
            {
                try
                {
                    currentPhase = "Provisionnement BATCH des partages utilisateurs";
                    _logger.LogInformation($"[BATCH] Début du provisionnement de {batchProvisionActions.Count} partages utilisateurs");
                    
                    await signalRService.SendImportProgressAsync(connectionId, processedCount, sortedActions.Count, 
                        $"{currentPhase} - Traitement de {batchProvisionActions.Count} partages...");
                    
                    var batchResults = await ExecuteProvisionUserShareBatchAsync(batchProvisionActions);
                    
                    // Ajouter les résultats du batch
                    foreach (var batchResult in batchResults)
                    {
                        processedCount++;
                        result.Results.Add(batchResult);
                        
                        if (batchResult.Success)
                        {
                            result.SuccessCount++;
                            result.ProvisionShareCount++;
                        }
                        else
                        {
                            result.ErrorCount++;
                        }
                        
                        // Mise à jour de la progression
                        await signalRService.SendImportProgressAsync(connectionId, processedCount, sortedActions.Count,
                            $"{currentPhase} - {batchResult.ObjectName} - ({result.SuccessCount} réussis, {result.ErrorCount} échecs)");
                    }
                    
                    _logger.LogInformation($"[BATCH] Provisionnement terminé: {result.ProvisionShareCount} partages créés");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Erreur lors du provisionnement batch: {ex.Message}");
                    
                    // En cas d'erreur batch, marquer toutes les actions comme échouées
                    foreach (var action in batchProvisionActions)
                    {
                        processedCount++;
                        result.Results.Add(new ImportActionResult
                        {
                            Success = false,
                            ActionType = action.ActionType,
                            ObjectName = action.ObjectName,
                            Path = action.Path,
                            Message = $"Erreur batch: {ex.Message}",
                            Exception = ex,
                            StartTime = DateTime.Now,
                            EndTime = DateTime.Now
                        });
                        result.ErrorCount++;
                    }
                }
            }

            result.ProcessedCount = processedCount;
            result.Message = $"Import terminé avec {result.SuccessCount} succès et {result.ErrorCount} erreurs";
            
            await signalRService.SendImportCompletedAsync(connectionId, result);
            _logger.LogInformation($"Import terminé: {result.Message}");
            
            return result;
        }

        private List<ImportAction> SortActionsByPriority(List<ImportAction> actions)
        {
            var priorityMap = new Dictionary<ActionType, int>
            {
                { ActionType.CREATE_OU, 1 },
                { ActionType.UPDATE_OU, 2 },
                { ActionType.CREATE_USER, 3 },
                { ActionType.UPDATE_USER, 4 },
                { ActionType.MOVE_USER, 5 },
                { ActionType.CREATE_STUDENT_FOLDER, 6 },
                { ActionType.CREATE_CLASS_GROUP_FOLDER, 7 },
                { ActionType.CREATE_TEAM, 90 },
                { ActionType.DELETE_USER, 95 },
                { ActionType.DELETE_OU, 99 },
                { ActionType.ERROR, 100 }
            };

            return actions
                .OrderBy(a => priorityMap.ContainsKey(a.ActionType) ? priorityMap[a.ActionType] : 50)
                .ToList();
        }

        private string GetPhaseNameForAction(ActionType actionType)
        {
            return actionType switch
            {
                ActionType.CREATE_OU => "Création d'unités organisationnelles",
                ActionType.UPDATE_OU => "Mise à jour d'unités organisationnelles",
                ActionType.CREATE_USER => "Création d'utilisateurs",
                ActionType.UPDATE_USER => "Mise à jour d'utilisateurs",
                ActionType.DELETE_USER => "Suppression d'utilisateurs",
                ActionType.DELETE_OU => "Suppression d'OUs vides",
                ActionType.MOVE_USER => "Déplacement d'utilisateurs",
                ActionType.CREATE_STUDENT_FOLDER => "Configuration des partages utilisateurs",
                ActionType.CREATE_CLASS_GROUP_FOLDER => "Création des dossiers de groupes de classes",
                ActionType.CREATE_TEAM => "Création des groupes Teams",
                _ => "Action en cours"
            };
        }

        private void UpdateCountsAndSendProgress(ImportResult currentImportResult, ImportActionResult actionResult, ImportAction action, int processedCount, int totalCount, ISignalRService signalRService, string? connectionId, string currentPhase)
        {
            if (actionResult.Success)
            {
                currentImportResult.SuccessCount++;
                
                switch (action.ActionType)
                {
                    case ActionType.CREATE_USER:
                        currentImportResult.CreateCount++;
                        break;
                    case ActionType.UPDATE_USER:
                        currentImportResult.UpdateCount++;
                        break;
                    case ActionType.DELETE_USER:
                        currentImportResult.DeleteCount++;
                        break;
                    case ActionType.CREATE_OU:
                        currentImportResult.CreateOUCount++;
                        break;
                    case ActionType.DELETE_OU:
                        currentImportResult.DeleteOUCount++;
                        break;
                    case ActionType.MOVE_USER:
                        currentImportResult.MoveCount++;
                        break;
                    case ActionType.CREATE_STUDENT_FOLDER:
                        currentImportResult.ProvisionShareCount++;
                        break;
                    case ActionType.CREATE_CLASS_GROUP_FOLDER:
                        currentImportResult.CreateClassGroupFolderCount++;
                        break;
                    case ActionType.CREATE_TEAM:
                        currentImportResult.CreateTeamGroupCount++;
                        break;
                }
            }
            else
            {
                currentImportResult.ErrorCount++;
            }
            
            signalRService.SendImportProgressAsync(connectionId, processedCount, totalCount, 
                $"{currentPhase} - {action.ObjectName} - ({currentImportResult.SuccessCount} réussis, {currentImportResult.ErrorCount} échecs)")
                .ConfigureAwait(false);
        }

        private async Task<ImportActionResult> ExecuteImportActionAsync(ImportAction action, ImportResult result, ImportConfig config)
        {
            _logger.LogInformation($"Exécution de l'action {action.ActionType} pour {action.ObjectName} dans {action.Path}");
            
            var actionResult = new ImportActionResult
            {
                ActionType = action.ActionType,
                ObjectName = action.ObjectName,
                Path = action.Path,
                StartTime = DateTime.Now,
                Success = true
            };

            try
            {
                switch (action.ActionType)
                {
                    
                    case ActionType.CREATE_OU:
                        await _ldapService.CreateOrganizationalUnitAsync(action.Path);
                        actionResult.Message = $"Création de l'unité organisationnelle '{action.ObjectName}' réussie";
                        break;
                    case ActionType.CREATE_GROUP:
                        try
                        {
                            string groupName = action.ObjectName;
                            string ouDn = action.Path;
                            bool isSecurity = true;
                            if (action.Attributes.TryGetValue("isSecurity", out var isSecStr))
                                bool.TryParse(isSecStr, out isSecurity);
                            bool isGlobal = true;
                            if (action.Attributes.TryGetValue("isGlobal", out var isGlobStr))
                                bool.TryParse(isGlobStr, out isGlobal);

                            // Description personnalisée si configurée
                            string? description = null;
                            if (config.GroupManagement != null && !string.IsNullOrWhiteSpace(config.GroupManagement.GroupDescriptionTemplate))
                            {
                                description = config.GroupManagement.GroupDescriptionTemplate.Replace("{OU}", ouDn);
                            }

                            string? groupDn = null;
                            if (config.GroupManagement != null && config.GroupManagement.EnableGroupNesting && !string.IsNullOrWhiteSpace(config.GroupManagement.GroupNestingTarget))
                            {
                                groupDn = config.GroupManagement.GroupNestingTarget;
                            }
                            _ldapService.CreateGroup(groupName, ouDn, isSecurity, isGlobal, description, groupDn);

                            actionResult.Success = true;
                            actionResult.Message = $"Groupe '{groupName}' créé dans '{ouDn}' (Sécurité: {isSecurity}, Global: {isGlobal})";

                            // Ajout automatique du groupe comme membre d'un groupe parent si configuré
                            /*if (config.GroupManagement != null && config.GroupManagement.EnableGroupNesting && !string.IsNullOrWhiteSpace(config.GroupManagement.GroupNestingTarget))
                            {
                                string childGroupDn = $"CN={groupName},{ouDn}";
                                string parentGroupDn = config.GroupManagement.GroupNestingTarget;
                                _ldapService.AddGroupToGroup(childGroupDn, parentGroupDn);
                                actionResult.Message += $" + membre de '{parentGroupDn}'";
                            }*/
                        }
                        catch (Exception exGroup)
                        {
                            actionResult.Success = false;
                            actionResult.Message = $"Erreur lors de la création du groupe : {exGroup.Message}";
                        }
                        break;
                    case ActionType.DELETE_OU:
                        var isEmpty = await _ldapService.IsOrganizationalUnitEmptyAsync(action.Path);
                        if (isEmpty)
                        {
                            await _ldapService.DeleteOrganizationalUnitAsync(action.Path);
                            actionResult.Success = true;
                            actionResult.Message = $"Suppression de l'unité organisationnelle vide '{action.ObjectName}' réussie";
                        }
                        else
                        {
                            actionResult.Success = false;
                            actionResult.Message = $"L'unité organisationnelle '{action.ObjectName}' n'est pas vide et ne peut pas être supprimée";
                        }
                        break;
                        
                    case ActionType.CREATE_USER:
                        await _ldapService.CreateUserAsync(action.Attributes, action.Path);
                        await _logService.LogActionAsync(LogAction.UserCreated, action.ObjectName, $"Création de l'utilisateur dans {action.Path}");
                        actionResult.Message = $"Création de l'utilisateur '{action.ObjectName}' réussie";
                        break;
                        
                    case ActionType.UPDATE_USER:
                        bool updatePerformed = await _ldapService.CompareAndUpdateUserAsync(action.ObjectName, action.Attributes, action.Path);
                        
                        if (updatePerformed)
                        {
                            await _logService.LogActionAsync(LogAction.UserUpdated, action.ObjectName, $"Mise à jour de l'utilisateur dans {action.Path}");
                            actionResult.Message = $"✅ Mise à jour de l'utilisateur '{action.ObjectName}' réussie (modifications appliquées)";
                        }
                        else
                        {
                            // Aucune mise à jour nécessaire - considérer comme succès
                            actionResult.Message = $"⏭️ Utilisateur '{action.ObjectName}' déjà à jour (aucune modification nécessaire)";
                        }
                        break;
                        
                    case ActionType.DELETE_USER:
                        await _ldapService.DeleteUserAsync(action.ObjectName, action.Path);
                        await _logService.LogActionAsync(LogAction.UserDeleted, action.ObjectName, $"Suppression de l'utilisateur");
                        actionResult.Message = $"Suppression de l'utilisateur '{action.ObjectName}' réussie";
                        break;
                        
                    case ActionType.MOVE_USER:
                        try
                        {
                            string sourceOu = action.Attributes.GetValueOrDefault("SourceOU", "");
                            string targetOu = action.Path;
                            
                            if (string.IsNullOrWhiteSpace(sourceOu) || string.IsNullOrWhiteSpace(targetOu))
                            {
                                actionResult.Success = false;
                                actionResult.Message = "Paramètres manquants pour le déplacement : SourceOU ou TargetOU";
                                break;
                            }
                            
                            await _ldapService.MoveUserAsync(action.ObjectName, sourceOu, targetOu);
                            await _logService.LogActionAsync(LogAction.UserMoved, action.ObjectName, $"Déplacement de {sourceOu} vers {targetOu}");
                            actionResult.Message = $"✅ Déplacement de l'utilisateur '{action.ObjectName}' de '{sourceOu}' vers '{targetOu}' réussi";
                        }
                        catch (Exception exMove)
                        {
                            actionResult.Success = false;
                            actionResult.Message = $"❌ Erreur lors du déplacement de l'utilisateur : {exMove.Message}";
                        }
                        break;
                        
                    case ActionType.CREATE_STUDENT_FOLDER:
                        if (!action.Attributes.TryGetValue("ServerName", out var serverName) || 
                            !action.Attributes.TryGetValue("LocalPathForUserShareOnServer", out var localPath) || 
                            !action.Attributes.TryGetValue("AccountAd", out var accountAd))
                        {
                            actionResult.Message = "Paramètres manquants pour le provisionnement du partage utilisateur";
                            break;
                        }
                        
                        // ✅ Correction : Convertir le JSON en List<string>
                        string subfoldersJson = action.Attributes.ContainsKey("Subfolders") ? action.Attributes["Subfolders"] : "[]";
                        List<string> subfolders;
                        try
                        {
                            subfolders = System.Text.Json.JsonSerializer.Deserialize<List<string>>(subfoldersJson) ?? new List<string>();
                        }
                        catch
                        {
                            subfolders = new List<string>(); // Défaut si la désérialisation échoue
                        }
                        
                        // ✅ Correction : Utiliser ShareNameForUserFolders au lieu de action.ObjectName pour le 3e paramètre
                        string shareNameForUserFolders = action.Attributes.ContainsKey("ShareNameForUserFolders") ? 
                            action.Attributes["ShareNameForUserFolders"] : "Data";
                        
                        // 🚀 NOUVEAU : Utiliser la méthode AlphaFS améliorée
                        _logger.LogInformation("Exécution de l'action PROVISION_USER_SHARE pour {ShareName} dans {LocalPath}", action.ObjectName, localPath);
                        
                        var folderProvisionResult = await _folderManagementService.ProvisionUserShareAsync(
                            serverName, 
                            localPath, 
                            shareNameForUserFolders, // ✅ Fixé : utilise le nom du partage principal
                            accountAd,
                            subfolders);
                            
                        actionResult.Success = folderProvisionResult;
                        actionResult.Message = folderProvisionResult ? "✅ Provisionnement AlphaFS du partage utilisateur réussi" : "❌ Erreur lors du provisionnement AlphaFS du partage utilisateur";
                        break;
                        
                    case ActionType.CREATE_CLASS_GROUP_FOLDER:
                        // Stub d'implémentation pour la création de dossier de groupe de classe
                        actionResult.Message = $"Création du dossier pour le groupe de classe '{action.ObjectName}' (simulée)";
                        break;
   
                        
                    case ActionType.ADD_USER_TO_GROUP:
                        try
                        {
                            string userDn = action.Attributes["userDn"];
                            string groupName = action.Attributes["groupName"];
                            string ouDn = action.Attributes["ouDn"];
                            string groupDn = $"CN={groupName},{ouDn}";
                            _ldapService.AddUserToGroup(userDn, groupDn);
                            actionResult.Success = true;
                            actionResult.Message = $"Utilisateur ajouté au groupe.";
                        }
                        catch (Exception exAdd)
                        {
                            actionResult.Success = false;
                            actionResult.Message = $"Erreur lors de l'ajout au groupe : {exAdd.Message}";
                        }
                        break;
                        
                    case ActionType.CREATE_TEAM:
                        // Exécuter l'intégration Teams
                        bool teamsSuccess = await ExecuteTeamsIntegrationAsync(action, result);
                        actionResult.Success = teamsSuccess;
                        actionResult.Message = teamsSuccess 
                            ? $"✅ Intégration Teams pour '{action.ObjectName}' réussie" 
                            : $"❌ Intégration Teams pour '{action.ObjectName}' échouée";
                        break;
                        
                    case ActionType.ERROR:
                        actionResult.Success = false;
                        actionResult.Message = action.Message;
                        break;
                        
                    default:
                        actionResult.Success = false;
                        actionResult.Message = $"Type d'action non pris en charge: {action.ActionType}";
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors de l'exécution de {action.ActionType} pour {action.ObjectName}: {ex.Message}");
                actionResult.Success = false;
                actionResult.Message = $"Erreur: {ex.Message}";
                actionResult.Exception = ex;
            }
            finally
            {
                actionResult.EndTime = DateTime.Now;
                actionResult.Duration = actionResult.EndTime - actionResult.StartTime;
            }

            return actionResult;
        }
        
        // 🚀 NOUVELLE MÉTHODE BATCH pour le provisionnement des partages utilisateurs
        private async Task<List<ImportActionResult>> ExecuteProvisionUserShareBatchAsync(List<ImportAction> provisionActions)
        {
            var results = new List<ImportActionResult>();
            
            if (!provisionActions.Any())
                return results;
            
            _logger.LogInformation($"[BATCH] Début du provisionnement batch pour {provisionActions.Count} partages utilisateurs");
            
            try
            {
                // Grouper les actions par serveur pour optimiser les connexions WMI
                var actionsByServer = provisionActions
                    .GroupBy(a => a.Attributes.GetValueOrDefault("ServerName", ""))
                    .ToList();
                
                foreach (var serverGroup in actionsByServer)
                {
                    var serverName = serverGroup.Key;
                    var serverActions = serverGroup.ToList();
                    
                    _logger.LogInformation($"[BATCH] Traitement de {serverActions.Count} partages sur le serveur {serverName}");
                    
                    // Préparer les données pour le batch
                    var batchRequests = new List<(string serverName, string localPath, string shareName, string accountAd, List<string> subfolders, ImportAction originalAction)>();
                    
                    foreach (var action in serverActions)
                    {
                        try
                        {
                            // Extraire les paramètres de l'action
                            if (!action.Attributes.TryGetValue("LocalPathForUserShareOnServer", out var localPath) || 
                                !action.Attributes.TryGetValue("AccountAd", out var accountAd))
                            {
                                results.Add(new ImportActionResult
                                {
                                    Success = false,
                                    ActionType = action.ActionType,
                                    ObjectName = action.ObjectName,
                                    Path = action.Path,
                                    Message = "Paramètres manquants pour le provisionnement du partage utilisateur",
                                    StartTime = DateTime.Now,
                                    EndTime = DateTime.Now
                                });
                                continue;
                            }
                            
                            // Convertir les sous-dossiers JSON en List<string>
                            string subfoldersJson = action.Attributes.GetValueOrDefault("Subfolders", "[]");
                            List<string> subfolders;
                            try
                            {
                                subfolders = System.Text.Json.JsonSerializer.Deserialize<List<string>>(subfoldersJson) ?? new List<string>();
                            }
                            catch
                            {
                                subfolders = new List<string>();
                            }
                            
                            string shareNameForUserFolders = action.Attributes.GetValueOrDefault("ShareNameForUserFolders", "Data");
                            
                            batchRequests.Add((serverName, localPath, shareNameForUserFolders, accountAd, subfolders, action));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"[BATCH] Erreur lors de la préparation de l'action pour {action.ObjectName}");
                            results.Add(new ImportActionResult
                            {
                                Success = false,
                                ActionType = action.ActionType,
                                ObjectName = action.ObjectName,
                                Path = action.Path,
                                Message = $"Erreur de préparation: {ex.Message}",
                                StartTime = DateTime.Now,
                                EndTime = DateTime.Now,
                                Exception = ex
                            });
                        }
                    }
                    
                    // Exécuter le batch par groupes de 50 pour éviter les timeouts
                    var batchGroups = batchRequests.Chunk(50);
                    
                    foreach (var batchGroup in batchGroups)
                    {
                        _logger.LogInformation($"[BATCH] Exécution d'un sous-lot de {batchGroup.Count()} partages sur {serverName}");
                        
                        var tasks = batchGroup.Select(async req =>
                        {
                            var startTime = DateTime.Now;
                            try
                            {
                                var success = await _folderManagementService.ProvisionUserShareAsync(
                                    req.serverName,
                                    req.localPath,
                                    req.shareName,
                                    req.accountAd,
                                    req.subfolders
                                );
                                
                                return new ImportActionResult
                                {
                                    Success = success,
                                    ActionType = req.originalAction.ActionType,
                                    ObjectName = req.originalAction.ObjectName,
                                    Path = req.originalAction.Path,
                                    Message = success ? "✅ Provisionnement batch du partage utilisateur réussi" : "❌ Erreur lors du provisionnement batch du partage utilisateur",
                                    StartTime = startTime,
                                    EndTime = DateTime.Now
                                };
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"[BATCH] Erreur lors du provisionnement de {req.originalAction.ObjectName}");
                                return new ImportActionResult
                                {
                                    Success = false,
                                    ActionType = req.originalAction.ActionType,
                                    ObjectName = req.originalAction.ObjectName,
                                    Path = req.originalAction.Path,
                                    Message = $"❌ Erreur batch: {ex.Message}",
                                    StartTime = startTime,
                                    EndTime = DateTime.Now,
                                    Exception = ex
                                };
                            }
                        });
                        
                        // Attendre que tous les partages du sous-lot soient traités
                        var batchResults = await Task.WhenAll(tasks);
                        results.AddRange(batchResults);
                        
                        // Pause entre les sous-lots
                        await Task.Delay(200);
                    }
                }
                
                var successCount = results.Count(r => r.Success);
                var errorCount = results.Count(r => !r.Success);
                _logger.LogInformation($"[BATCH] Provisionnement batch terminé: {successCount} succès, {errorCount} erreurs");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[BATCH] Erreur globale lors du provisionnement batch: {ex.Message}");
                
                // En cas d'erreur globale, créer des résultats d'échec pour les actions non traitées
                var unprocessedActions = provisionActions.Skip(results.Count);
                foreach (var action in unprocessedActions)
                {
                    results.Add(new ImportActionResult
                    {
                        Success = false,
                        ActionType = action.ActionType,
                        ObjectName = action.ObjectName,
                        Path = action.Path,
                        Message = $"Erreur batch globale: {ex.Message}",
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now,
                        Exception = ex
                    });
                }
            }
            
            return results;
        }

        /// <summary>
        /// 👈 NOUVELLE MÉTHODE : Exécution de l'intégration Teams
        /// </summary>
        /// <returns>True si l'intégration Teams a réussi, false sinon</returns>
        private async Task<bool> ExecuteTeamsIntegrationAsync(ImportAction action, ImportResult result)
        {
            try
            {
                if (_teamsIntegrationService == null)
                {
                    _logger.LogInformation("🔕 Service Teams Integration non disponible pour action {ActionName} - Intégration ignorée", action.ObjectName);
                    result.Warnings.Add($"Service Teams non disponible pour {action.ObjectName}");
                    return false;
                }

                var ouName = action.Attributes.GetValueOrDefault("ouName", action.ObjectName);
                var ouPath = action.Attributes.GetValueOrDefault("ouPath", action.Path);
                
                _logger.LogInformation("🚀 Exécution intégration Teams pour OU '{OUName}'", ouName);

                var teamsResult = await _teamsIntegrationService.CreateTeamFromOUAsync(ouName, ouPath);
                
                // Ajouter les warnings de Teams au résultat global
                foreach (var warning in teamsResult.Warnings)
                {
                    result.Warnings.Add($"Teams - {ouName}: {warning}");
                }
                
                if (teamsResult.Success)
                {
                    result.CreatedTeams++;
                    result.Messages.Add($"✅ Équipe Teams créée pour {ouName}: {teamsResult.TeamId}");
                    _logger.LogInformation("✅ Équipe Teams créée pour OU '{OUName}': {TeamId}", ouName, teamsResult.TeamId);
                    return true;
                }
                else
                {
                    result.Warnings.Add($"⚠️ Échec création Teams pour {ouName}: {teamsResult.ErrorMessage}");
                    _logger.LogInformation("⚠️ Échec création Teams pour OU '{OUName}': {Error}", ouName, teamsResult.ErrorMessage);
                    return false;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"❌ Erreur Teams pour {action.ObjectName}: {ex.Message}");
                _logger.LogError(ex, "❌ Erreur lors de l'exécution Teams pour action {ActionName}", action.ObjectName);
                return false;
            }
        }

        public async Task<List<OrganizationalUnitModel>> GetAllOrganizationalUnitsAsync()
        {
            return await _ldapService.GetAllOrganizationalUnitsAsync();
        }

        public async Task<ImportResult> ProcessSpreadsheetDataAsync(List<Dictionary<string, string>> data, ImportConfig config)
        {
            try
            {
                var analysisResult = await AnalyzeSpreadsheetDataAsync(data, config);
                
                if (!analysisResult.Success || analysisResult.Analysis == null)
                {
                    return new ImportResult
                    {
                        Success = false,
                        Message = analysisResult.ErrorMessage ?? "Erreur lors de l'analyse des données"
                    };
                }
                
                return await ExecuteImportFromAnalysisAsync(analysisResult.Analysis, config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors du traitement des données de tableur: {ex.Message}");
                return new ImportResult
                {
                    Success = false,
                    Message = $"Erreur lors du traitement des données: {ex.Message}"
                };
            }
        }

        #endregion
    }
}