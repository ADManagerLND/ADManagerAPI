using ADManagerAPI.Models;

// 👈 AJOUT pour ITeamsIntegrationService

namespace ADManagerAPI.Services;

public partial class SpreadsheetImportService
{
    #region Gestion des unités organisationnelles

    protected bool ShouldProcessOrganizationalUnits(ImportConfig config)
    {
        return config.CreateMissingOUs && !string.IsNullOrEmpty(config.ouColumn);
    }

    private async Task ProcessOrganizationalUnitsAsync(List<Dictionary<string, string>> spreadsheetData,
        ImportConfig config, ImportAnalysis analysis, string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"Analyse des OUs depuis la colonne '{config.ouColumn}'");
        var defaultOuExists = await EnsureDefaultOuExistsAsync(config, analysis);

        if (string.IsNullOrEmpty(config.DefaultOU) || defaultOuExists)
        {
            var uniqueOuValues = ExtractUniqueOuValues(spreadsheetData, config);
            var existingOus = await GetExistingOusAsync(uniqueOuValues, config);
            CreateOuActions(uniqueOuValues, existingOus, config, analysis);
        }
    }

    protected async Task<bool> EnsureDefaultOuExistsAsync(ImportConfig config, ImportAnalysis analysis)
    {
        if (string.IsNullOrEmpty(config.DefaultOU))
            return true;

        var defaultOuExists = await CheckOrganizationalUnitExistsAsync(config.DefaultOU);
        if (!defaultOuExists && config.CreateMissingOUs)
        {
            var defaultOuName = ExtractOuName(config.DefaultOU);

            // 🆕 Vérifier qu'il n'y a pas déjà une action CREATE_OU pour cette OU
            var alreadyHasAction = analysis.Actions.Any(a =>
                a.ActionType == ActionType.CREATE_OU &&
                a.Path.Equals(config.DefaultOU, StringComparison.OrdinalIgnoreCase));

            if (!alreadyHasAction)
                analysis.Actions.Add(new ImportAction
                {
                    ActionType = ActionType.CREATE_OU,
                    ObjectName = defaultOuName,
                    Path = config.DefaultOU,
                    Message = $"Création de l'unité organisationnelle parent '{defaultOuName}'",
                    Attributes = new Dictionary<string, string>
                    {
                        { "ouName", defaultOuName },
                        { "ouPath", config.DefaultOU },
                        { "createTeams", "true" } // Flag pour intégration Teams
                    }
                });
            return true;
        }

        return defaultOuExists;
    }

    protected List<string> ExtractUniqueOuValues(List<Dictionary<string, string>> spreadsheetData, ImportConfig config)
    {
        var extractedValues = spreadsheetData
            .Where(row => row.ContainsKey(config.ouColumn) && !string.IsNullOrEmpty(row[config.ouColumn]))
            .Select(row =>
            {
                var rawValue = row[config.ouColumn];
                var trimmedValue = rawValue.Trim();
                return trimmedValue;
            })
            .Where(trimmedValue => !string.IsNullOrEmpty(trimmedValue))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return extractedValues;
    }

    private async Task<HashSet<string>> GetExistingOusAsync(List<string> uniqueOuValues, ImportConfig config)
    {
        var existingOus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = uniqueOuValues.Select(async ouValueFromCsv =>
        {
            var ouPathBuilt = BuildOuPath(ouValueFromCsv, config.DefaultOU);

            var exists = await CheckOrganizationalUnitExistsAsync(ouPathBuilt);

            return new { OuPath = ouPathBuilt, Exists = exists, OriginalCsvValue = ouValueFromCsv };
        });

        var results = await Task.WhenAll(tasks);
        foreach (var result in results)
            if (result.Exists)
                existingOus.Add(result.OuPath);

        return existingOus;
    }

    protected void CreateOuActions(List<string> uniqueOuValuesFromCsv, HashSet<string> existingOuDns,
        ImportConfig config, ImportAnalysis analysis)
    {
        foreach (var ouValueCsv in uniqueOuValuesFromCsv)
        {
            var ouPathTarget = BuildOuPath(ouValueCsv, config.DefaultOU);

            if (!existingOuDns.Contains(ouPathTarget))
            {
                var objectNameForAction = ExtractOuName(ouValueCsv);

                // 🆕 Une seule action CREATE_OU avec flag Teams
                analysis.Actions.Add(new ImportAction
                {
                    ActionType = ActionType.CREATE_OU,
                    ObjectName = objectNameForAction,
                    Path = ouPathTarget,
                    Message =
                        $"Création de l'unité organisationnelle '{objectNameForAction}' sous '{ExtractParentDnFromPath(ouPathTarget)}'",
                    Attributes = new Dictionary<string, string>
                    {
                        { "ouName", objectNameForAction },
                        { "ouPath", ouPathTarget },
                        { "createTeams", "true" } // Flag pour indiquer qu'il faut créer l'équipe Teams
                    }
                });

                // 🆕 Utiliser la méthode centralisée pour les groupes
                AddGroupCreationActions(objectNameForAction, ouPathTarget, config, analysis);
            }
        }
    }

    /// <summary>
    ///     Méthode centralisée pour ajouter les actions de création de groupes
    /// </summary>
    protected void AddGroupCreationActions(string ouName, string ouPath, ImportConfig config, ImportAnalysis analysis)
    {
        var groupPrefix = config.GroupPrefix ?? string.Empty;
        var groupSecName = string.IsNullOrEmpty(groupPrefix) ? $"Sec_{ouName}" : $"{groupPrefix}Sec_{ouName}";
        var groupDistName = string.IsNullOrEmpty(groupPrefix) ? $"Dist_{ouName}" : $"{groupPrefix}Dist_{ouName}";

        analysis.Actions.Add(new ImportAction
        {
            ActionType = ActionType.CREATE_GROUP,
            ObjectName = groupSecName,
            Path = ouPath,
            Message = $"Création du groupe de sécurité '{groupSecName}' dans l'OU '{ouName}'",
            Attributes = new Dictionary<string, string> { { "isSecurity", "true" }, { "isGlobal", "true" } }
        });

        analysis.Actions.Add(new ImportAction
        {
            ActionType = ActionType.CREATE_GROUP,
            ObjectName = groupDistName,
            Path = ouPath,
            Message = $"Création du groupe de distribution '{groupDistName}' dans l'OU '{ouName}'",
            Attributes = new Dictionary<string, string> { { "isSecurity", "false" }, { "isGlobal", "true" } }
        });
    }

    private async Task ProcessEmptyOrganizationalUnitsAsync(List<string> scannedOus, ImportConfig config,
        ImportAnalysis analysis, CancellationToken cancellationToken = default)
    {
        if (!scannedOus.Any()) 
        {
            _logger.LogInformation("🔍 Aucune OU à scanner pour vérifier si elles sont vides");
            return;
        }

        _logger.LogInformation($"🔍 Scan de {scannedOus.Count} OU(s) pour détecter celles qui sont vides d'utilisateurs...");
        var totalEmptyOUs = 0;

        foreach (var ouPath in scannedOus)
        {
            try
            {
                var ouName = ExtractOuName(ouPath);
                
                // 🛡️ PROTECTION : Ne jamais supprimer l'OU racine ou les OUs principales
                if (IsRootOrMainOU(ouPath, config))
                {
                    _logger.LogInformation($"🛡️ OU '{ouName}' protégée contre la suppression (OU racine ou principale) - ignorée");
                    continue;
                }
                
                // ✅ CORRECTION : Tester d'abord si l'OU ne contient que des groupes, puis si elle est complètement vide
                var isEmptyOfUsers = await _ldapService.IsOrganizationalUnitEmptyOfUsersAsync(ouPath);
                
                _logger.LogInformation($"🔍 OU '{ouName}': vide d'utilisateurs = {isEmptyOfUsers}");
                
                if (isEmptyOfUsers)
                {
                    // Vérifier s'il ne reste que des groupes ou si c'est complètement vide
                    var groupsInOU = await _ldapService.GetGroupsInOUAsync(ouPath);
                    
                    _logger.LogInformation($"🔍 OU '{ouName}': {groupsInOU.Count} groupes trouvés");
                    
                    if (groupsInOU.Any())
                    {
                        // OU qui ne contient que des groupes (pas d'utilisateurs)
                        analysis.Actions.Add(new ImportAction
                        {
                            ActionType = ActionType.DELETE_OU,
                            ObjectName = ouName,
                            Path = ouPath,
                            Message = $"Suppression de l'unité organisationnelle '{ouName}' (ne contient que {groupsInOU.Count} groupe(s), aucun utilisateur)",
                            Attributes = new Dictionary<string, string>
                            {
                                ["Reason"] = "Contient seulement des groupes",
                                ["GroupCount"] = groupsInOU.Count.ToString(),
                                ["OUPath"] = ouPath
                            }
                        });
                        
                        totalEmptyOUs++;
                        _logger.LogInformation($"➕ OU sans utilisateurs détectée: {ouName} ({groupsInOU.Count} groupes, {ouPath})");
                    }
                    else
                    {
                        // Vérifier si c'est complètement vide
                        var isCompletelyEmpty = await _ldapService.IsOrganizationalUnitEmptyAsync(ouPath);
                        _logger.LogInformation($"🔍 OU '{ouName}': complètement vide = {isCompletelyEmpty}");
                        
                        if (isCompletelyEmpty)
                        {
                            analysis.Actions.Add(new ImportAction
                            {
                                ActionType = ActionType.DELETE_OU,
                                ObjectName = ouName,
                                Path = ouPath,
                                Message = $"Suppression de l'unité organisationnelle complètement vide '{ouName}'",
                                Attributes = new Dictionary<string, string>
                                {
                                    ["Reason"] = "Complètement vide",
                                    ["OUPath"] = ouPath
                                }
                            });
                            
                            totalEmptyOUs++;
                            _logger.LogInformation($"➕ OU complètement vide détectée: {ouName} ({ouPath})");
                        }
                        else
                        {
                            _logger.LogInformation($"ℹ️ OU '{ouName}' sans utilisateurs mais contient d'autres objets (non groupes) - non supprimée");
                        }
                    }
                }
                else
                {
                    _logger.LogInformation($"👥 OU '{ouName}' contient des utilisateurs - non supprimée");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"⚠️ Erreur lors de la vérification de l'OU '{ouPath}': {ex.Message}");
            }
        }

        if (totalEmptyOUs > 0)
        {
            _logger.LogInformation($"🗑️ {totalEmptyOUs} OU(s) vide(s) d'utilisateurs détectée(s) et marquée(s) pour suppression");
        }
        else
        {
            _logger.LogInformation("✅ Aucune OU vide d'utilisateurs détectée dans les OUs scannées");
        }
    }

    /// <summary>
    /// Vérifie si une OU est une OU racine ou principale qui ne doit jamais être supprimée
    /// </summary>
    private bool IsRootOrMainOU(string ouPath, ImportConfig config)
    {
        if (string.IsNullOrEmpty(ouPath))
            return false;

        // 1. Ne jamais supprimer l'OU par défaut configurée (OU racine)
        if (!string.IsNullOrEmpty(config.DefaultOU) && 
            ouPath.Equals(config.DefaultOU, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug($"🛡️ OU protégée: '{ouPath}' est l'OU par défaut configurée");
            return true;
        }

        // 2. Ne jamais supprimer les OUs qui sont directement sous le domaine (niveau racine)
        var parts = ouPath.Split(',');
        var ouParts = parts.Where(p => p.Trim().StartsWith("OU=", StringComparison.OrdinalIgnoreCase)).ToArray();
        
        // Si c'est une OU de premier niveau (directement sous le domaine)
        if (ouParts.Length == 1)
        {
            _logger.LogDebug($"🛡️ OU protégée: '{ouPath}' est une OU de premier niveau");
            return true;
        }

        // 3. Protection spéciale pour les OUs nommées couramment utilisées comme racines
        var ouName = ExtractOuName(ouPath).ToUpperInvariant();
        var protectedNames = new[] { "TEST", "USERS", "UTILISATEURS", "ELEVES", "ETUDIANTS", "PERSONNEL", "STAFF", "CLASSES" };
        
        if (protectedNames.Contains(ouName) && ouParts.Length <= 2) // OU racine ou de second niveau
        {
            _logger.LogDebug($"🛡️ OU protégée: '{ouName}' est dans la liste des noms protégés");
            return true;
        }

        return false;
    }

    #region Gestion des groupes vides

    /// <summary>
    /// Traite la suppression des groupes vides dans les OUs scannées
    /// </summary>
    private async Task ProcessEmptyGroupsAsync(List<string> scannedOus, ImportConfig config,
        ImportAnalysis analysis, CancellationToken cancellationToken = default)
    {
        if (!scannedOus.Any())
        {
            _logger.LogInformation("🔍 Aucune OU à scanner pour les groupes vides");
            return;
        }

        _logger.LogInformation($"🔍 Scan de {scannedOus.Count} OU(s) pour détecter les groupes vides...");
        var totalEmptyGroups = 0;

        foreach (var ouPath in scannedOus)
        {
            try
            {
                var groupsInOU = await _ldapService.GetGroupsInOUAsync(ouPath);
                _logger.LogDebug("🔍 OU '{OuPath}': {GroupCount} groupes trouvés", ouPath, groupsInOU.Count);

                foreach (var groupDn in groupsInOU)
                {
                    try
                    {
                        var isEmpty = await _ldapService.IsGroupEmptyAsync(groupDn);
                        if (isEmpty)
                        {
                            var groupName = ExtractGroupName(groupDn);
                            analysis.Actions.Add(new ImportAction
                            {
                                ActionType = ActionType.DELETE_GROUP,
                                ObjectName = groupName,
                                Path = groupDn,
                                Message = $"Suppression du groupe vide '{groupName}' dans '{ExtractOuName(ouPath)}'",
                                Attributes = new Dictionary<string, string>
                                {
                                    ["GroupDn"] = groupDn,
                                    ["OUPath"] = ouPath
                                }
                            });
                            
                            totalEmptyGroups++;
                            _logger.LogDebug($"➕ Groupe vide détecté: {groupName} ({groupDn})");
                        }
                        else
                        {
                            _logger.LogDebug($"👥 Groupe '{ExtractGroupName(groupDn)}' contient des membres - non supprimé");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"⚠️ Erreur lors de la vérification du groupe '{groupDn}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"⚠️ Erreur lors du scan des groupes dans l'OU '{ouPath}': {ex.Message}");
            }
        }

        if (totalEmptyGroups > 0)
        {
            _logger.LogInformation($"🗑️ {totalEmptyGroups} groupe(s) vide(s) détecté(s) et marqué(s) pour suppression");
        }
        else
        {
            _logger.LogInformation("✅ Aucun groupe vide détecté dans les OUs scannées");
        }
    }

    /// <summary>
    /// Extrait le nom d'un groupe à partir de son DN
    /// Exemple: "CN=Sec_1ALTO,OU=1ALTO,OU=TEST,DC=lycee,DC=nd" -> "Sec_1ALTO"
    /// </summary>
    private string ExtractGroupName(string groupDn)
    {
        if (string.IsNullOrEmpty(groupDn)) return string.Empty;
        
        var parts = groupDn.Split(',');
        if (parts.Length == 0) return groupDn;
        
        var cnPart = parts[0];
        if (cnPart.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            return cnPart.Substring(3);
        
        return cnPart;
    }

    #endregion

    #endregion
}