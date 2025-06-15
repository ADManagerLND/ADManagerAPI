using System.Text.Json;
using ADManagerAPI.Models;

// 👈 AJOUT pour ITeamsIntegrationService

namespace ADManagerAPI.Services
{
    public partial class SpreadsheetImportService
    {
        #region Gestion des unités organisationnelles

        protected bool ShouldProcessOrganizationalUnits(ImportConfig config)
        {
            return config.CreateMissingOUs && !string.IsNullOrEmpty(config.ouColumn);
        }

        private async Task ProcessOrganizationalUnitsAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, ImportAnalysis analysis, string? connectionId = null, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation($"Analyse des OUs depuis la colonne '{config.ouColumn}'");
            bool defaultOuExists = await EnsureDefaultOuExistsAsync(config, analysis);

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
                
            bool defaultOuExists = await CheckOrganizationalUnitExistsAsync(config.DefaultOU);
            if (!defaultOuExists && config.CreateMissingOUs)
            {
                string defaultOuName = ExtractOuName(config.DefaultOU);
                
                // 🆕 Vérifier qu'il n'y a pas déjà une action CREATE_OU pour cette OU
                bool alreadyHasAction = analysis.Actions.Any(a => 
                    a.ActionType == ActionType.CREATE_OU && 
                    a.Path.Equals(config.DefaultOU, StringComparison.OrdinalIgnoreCase));
                    
                if (!alreadyHasAction)
                {
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
                }
                return true; 
            }
            return defaultOuExists;
        }

        protected List<string> ExtractUniqueOuValues(List<Dictionary<string, string>> spreadsheetData, ImportConfig config)
        {
            var extractedValues = spreadsheetData
                .Where(row => row.ContainsKey(config.ouColumn) && !string.IsNullOrEmpty(row[config.ouColumn]))
                .Select(row => {
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
                string ouPathBuilt = BuildOuPath(ouValueFromCsv, config.DefaultOU);
      
                bool exists = await CheckOrganizationalUnitExistsAsync(ouPathBuilt);
     
                return new { OuPath = ouPathBuilt, Exists = exists, OriginalCsvValue = ouValueFromCsv };
            });

            var results = await Task.WhenAll(tasks);
            foreach (var result in results)
            {
                if (result.Exists)
                {
                    existingOus.Add(result.OuPath);
                }
            }
            return existingOus;
        }

        protected void CreateOuActions(List<string> uniqueOuValuesFromCsv, HashSet<string> existingOuDns, ImportConfig config, ImportAnalysis analysis)
        {
    
            foreach (var ouValueCsv in uniqueOuValuesFromCsv)
            {
                string ouPathTarget = BuildOuPath(ouValueCsv, config.DefaultOU); 
        
                if (!existingOuDns.Contains(ouPathTarget))
                {
                    string objectNameForAction = ExtractOuName(ouValueCsv);
            
                    // 🆕 Une seule action CREATE_OU avec flag Teams
                    analysis.Actions.Add(new ImportAction
                    {
                        ActionType = ActionType.CREATE_OU,
                        ObjectName = objectNameForAction, 
                        Path = ouPathTarget,
                        Message = $"Création de l'unité organisationnelle '{objectNameForAction}' sous '{ExtractParentDnFromPath(ouPathTarget)}'",
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
        /// Méthode centralisée pour ajouter les actions de création de groupes
        /// </summary>
        protected void AddGroupCreationActions(string ouName, string ouPath, ImportConfig config, ImportAnalysis analysis)
        {
            string groupPrefix = config.GroupPrefix ?? string.Empty;
            string groupSecName = string.IsNullOrEmpty(groupPrefix) ? $"Sec_{ouName}" : $"{groupPrefix}Sec_{ouName}";
            string groupDistName = string.IsNullOrEmpty(groupPrefix) ? $"Dist_{ouName}" : $"{groupPrefix}Dist_{ouName}";
            
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

        private async Task ProcessEmptyOrganizationalUnitsAsync(List<string> scannedOus, ImportConfig config, ImportAnalysis analysis, CancellationToken cancellationToken = default)
        {
            if (!scannedOus.Any())
            {
                return;
            }
            
            foreach (var ouPath in scannedOus)
            {
                try
                {
                    bool isEmpty = await _ldapService.IsOrganizationalUnitEmptyAsync(ouPath);
                    if (isEmpty)
                    {
                        string ouName = ExtractOuName(ouPath);
                        analysis.Actions.Add(new ImportAction
                        {
                            ActionType = ActionType.DELETE_OU,
                            ObjectName = ouName,
                            Path = ouPath,
                            Message = $"Suppression de l'unité organisationnelle vide '{ouName}'.",
                            Attributes = new Dictionary<string, string>()
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"[IMPORT EMPTY_OU] Erreur lors de la vérification de l'OU '{ouPath}': {ex.Message}");
                }
            }
        }

        #endregion
    }
}