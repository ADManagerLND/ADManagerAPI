using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Services.Parse;
using ADManagerAPI.Services.Utilities;
using System.IO;

namespace ADManagerAPI.Services
{
    public class CsvManagerService : ICsvManagerService
    {
        private readonly ILdapService _ldapService;
        private readonly ILogService _logService;
        private readonly ILogger<CsvManagerService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        
        private readonly IEnumerable<ISpreadsheetParserService> _parsers;
        
        private readonly bool _enableOrphanCleanup = true; // Mettez à false pour désactiver globalement cette fonctionnalité

        public CsvManagerService(
            IEnumerable<ISpreadsheetParserService> parsers,
            ILdapService ldapService, 
            ILogService logService, 
            ILogger<CsvManagerService> logger,
            IServiceScopeFactory serviceScopeFactory)
        {
            _parsers           = parsers ?? throw new ArgumentNullException(nameof(parsers));
            _ldapService = ldapService ?? throw new ArgumentNullException(nameof(ldapService));
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        }

        #region Analyse du CSV

        public async Task<CsvAnalysisResult> AnalyzeCsvContentAsync(Stream fileStream, string fileName, ImportConfig config)
        {
            if (fileStream == null || fileStream.Length == 0)
            {
                _logger.LogError("Le flux du fichier est vide ou null");
                return new CsvAnalysisResult
                {
                    Success = false,
                    ErrorMessage = "Le fichier fourni est vide ou invalide."
                };
            }

            _logger.LogInformation($"Nouvelle analyse de fichier ({fileName}) démarrée");

            try
            {
                config = ImportConfigHelpers.EnsureValidConfig(config, _logger);

                var parser = ChooseParser(fileName);
                
                if (parser == null)
                {
                    _logger.LogError("Aucun service d'analyse de feuille de calcul n'a pu être déterminé.");
                    return new CsvAnalysisResult
                    {
                        Success = false,
                        ErrorMessage = "Aucun service d'analyse de feuille de calcul n'a pu être déterminé pour le type de fichier."
                    };
                }

                var csvData = await parser.ParseAsync(fileStream, fileName, config.CsvDelimiter, config.ManualColumns);

                if (csvData.Count == 0)
                {
                    _logger.LogError("Aucune donnée valide trouvée dans le fichier CSV");
                    return new CsvAnalysisResult
                    {
                        Success = false,
                        ErrorMessage = "Aucune donnée valide n'a été trouvée dans le fichier CSV."
                    };
                }

                CsvDataStore.SetCsvData(csvData);
                return await AnalyzeCsvDataAsync(csvData, config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors de l'analyse du contenu CSV: {ex.Message}");
                return new CsvAnalysisResult
                {
                    Success = false,
                    ErrorMessage = $"Une erreur est survenue lors de l'analyse du fichier CSV: {ex.Message}"
                };
            }
        }

        public async Task<CsvAnalysisResult> AnalyzeCsvDataAsync(List<Dictionary<string, string>> csvData, ImportConfig config)
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Analyse de données CSV déjà chargées");

            try
            {
                if (csvData == null || csvData.Count == 0)
                {
                    _logger.LogError("Aucune donnée CSV fournie pour l'analyse");
                    return new CsvAnalysisResult
                    {
                        Success = false,
                        ErrorMessage = "Aucune donnée CSV n'a été fournie pour l'analyse."
                    };
                }

                config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
                var headers = csvData.FirstOrDefault()?.Keys.ToList() ?? new List<string>();
                var previewData = csvData.Take(10).ToList();

                var result = new CsvAnalysisResult
                {
                    Success = true,
                    CsvData = csvData,
                    CsvHeaders = headers,
                    PreviewData = previewData.Select(row => row as object).ToList(),
                    TableData = csvData,
                    Errors = new List<string>(),
                    IsValid = true,
                };

                var analysis = await AnalyzeCsvDataForActionsAsync(csvData, config);

                if (analysis != null)
                {
                    result.Analysis = analysis;
                    result.Summary = new
                    {
                        TotalRows = csvData.Count,
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
                _logger.LogInformation($"Analyse de données CSV terminée en {stopwatch.ElapsedMilliseconds} ms");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors de l'analyse des données CSV: {ex.Message}");
                return new CsvAnalysisResult
                {
                    Success = false,
                    ErrorMessage = $"Une erreur est survenue lors de l'analyse des données CSV: {ex.Message}"
                };
            }
        }

        public async Task<ImportAnalysis> AnalyzeCsvDataForActionsAsync(List<Dictionary<string, string>> csvData, ImportConfig config)
        {
            config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
            var analysis = new ImportAnalysis
            {
                Summary = new ImportSummary { TotalObjects = csvData.Count },
                Actions = new List<ImportAction>()
            };
            List<string> scannedOusForOrphanCleanup = new List<string>();

            try
            {
                if (ShouldProcessOrganizationalUnits(config))
                {
                    await ProcessOrganizationalUnitsAsync(csvData, config, analysis);
                }

                await ProcessUsersAsync(csvData, config, analysis);
                
                if (_enableOrphanCleanup) // Supposition: _enableOrphanCleanup contrôle aussi le nettoyage des OUs vides
                {
                    scannedOusForOrphanCleanup = await ProcessOrphanedUsersAsync(csvData, config, analysis);
                    // Après la suppression des utilisateurs, traiter les OUs vides
                    await ProcessEmptyOrganizationalUnitsAsync(scannedOusForOrphanCleanup, config, analysis);
                }

                UpdateAnalysisSummary(analysis);
                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de l'analyse des données CSV");
                throw;
            }
        }

        private bool ShouldProcessOrganizationalUnits(ImportConfig config)
        {
            return config.CreateMissingOUs && !string.IsNullOrEmpty(config.ouColumn);
        }

        private async Task ProcessOrganizationalUnitsAsync(List<Dictionary<string, string>> csvData, ImportConfig config, ImportAnalysis analysis)
        {
            _logger.LogInformation($"Analyse des OUs depuis la colonne '{config.ouColumn}'");
            bool defaultOuExists = await EnsureDefaultOuExistsAsync(config, analysis);

            if (string.IsNullOrEmpty(config.DefaultOU) || defaultOuExists)
            {
                var uniqueOuValues = ExtractUniqueOuValues(csvData, config);
                var existingOus = await GetExistingOusAsync(uniqueOuValues, config);
                CreateOuActions(uniqueOuValues, existingOus, config, analysis);
            }
        }

        private async Task<bool> EnsureDefaultOuExistsAsync(ImportConfig config, ImportAnalysis analysis)
        {
            if (string.IsNullOrEmpty(config.DefaultOU))
                return true;
                
            bool defaultOuExists = await CheckOrganizationalUnitExistsAsync(config.DefaultOU);
            if (!defaultOuExists && config.CreateMissingOUs)
            {
                string defaultOuName = ExtractOuName(config.DefaultOU);
                analysis.Actions.Add(new ImportAction
                {
                    ActionType = ActionType.CREATE_OU,
                    ObjectName = defaultOuName,
                    Path = config.DefaultOU,
                    Message = $"Création de l'unité organisationnelle parent '{defaultOuName}'",
                    Attributes = new Dictionary<string, string>()
                });
                return true; 
            }
            return defaultOuExists;
        }

        private string ExtractOuName(string ouPath)
        {
            if (string.IsNullOrEmpty(ouPath)) return string.Empty;
            string ouName = ouPath.Split(',')[0];
            if (ouName.StartsWith("OU="))
                ouName = ouName.Substring(3);
            return ouName.Trim();
        }

        private List<string> ExtractUniqueOuValues(List<Dictionary<string, string>> csvData, ImportConfig config)
        {
            _logger.LogDebug("[OU_DEBUG] Début de ExtractUniqueOuValues.");
            var extractedValues = csvData
                .Where(row => row.ContainsKey(config.ouColumn) && !string.IsNullOrEmpty(row[config.ouColumn]))
                .Select(row => {
                    var rawValue = row[config.ouColumn];
                    var trimmedValue = rawValue.Trim();
                    _logger.LogTrace($"[OU_DEBUG] Valeur OU brute du CSV: '{rawValue}', après Trim: '{trimmedValue}'");
                    return trimmedValue;
                })
                .Where(trimmedValue => !string.IsNullOrEmpty(trimmedValue))
                .Distinct(StringComparer.OrdinalIgnoreCase) 
                .ToList();
            _logger.LogInformation($"[OU_DEBUG] {extractedValues.Count} valeurs d'OU uniques (insensible à la casse, après Trim) extraites du CSV: {JsonSerializer.Serialize(extractedValues)}");
            return extractedValues;
        }

        private async Task<HashSet<string>> GetExistingOusAsync(List<string> uniqueOuValues, ImportConfig config)
        {
            _logger.LogDebug($"[OU_DEBUG] Début de GetExistingOusAsync pour {uniqueOuValues.Count} valeurs uniques.");
            var existingOus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tasks = uniqueOuValues.Select(async ouValueFromCsv =>
            {
                string ouPathBuilt = BuildOuPath(ouValueFromCsv, config.DefaultOU);
                _logger.LogDebug($"[OU_DEBUG_EXISTENCE] Test d'existence pour la valeur CSV normalisée '{ouValueFromCsv}', DN construit '{ouPathBuilt}'");
                
                bool exists = await CheckOrganizationalUnitExistsAsync(ouPathBuilt);
                _logger.LogInformation($"[OU_DEBUG_EXISTENCE] Résultat du test d'existence pour DN '{ouPathBuilt}': {exists}");
                
                return new { OuPath = ouPathBuilt, Exists = exists, OriginalCsvValue = ouValueFromCsv };
            });

            var results = await Task.WhenAll(tasks);
            foreach (var result in results)
            {
                if (result.Exists)
                {
                    existingOus.Add(result.OuPath);
                }
                else
                {
                    _logger.LogWarning($"[OU_DEBUG_EXISTENCE] L'OU avec DN construit '{result.OuPath}' (depuis valeur CSV '{result.OriginalCsvValue}') n'a pas été trouvée dans l'AD lors du scan initial.");
                }
            }
            _logger.LogInformation($"[OU_DEBUG] {existingOus.Count} OUs existantes trouvées dans l'AD (basées sur les DNs construits): {JsonSerializer.Serialize(existingOus)}");
            return existingOus;
        }

        private string BuildOuPath(string ouValueFromCsv, string defaultOu)
        {
             _logger.LogTrace($"[OU_DEBUG_BUILD] BuildOuPath appelé avec ouValueFromCsv: '{ouValueFromCsv}', defaultOu: '{defaultOu}'");
            string cleanDefaultOu = defaultOu?.Trim();

            if (string.IsNullOrEmpty(ouValueFromCsv))
            {
                _logger.LogWarning("[OU_DEBUG_BUILD] ouValueFromCsv est vide. Retour de defaultOu uniquement.");
                return cleanDefaultOu;
            }

            bool isLikelyDn = ouValueFromCsv.Contains("DC=", StringComparison.OrdinalIgnoreCase) ||
                              (ouValueFromCsv.Contains("OU=", StringComparison.OrdinalIgnoreCase) && ouValueFromCsv.Contains(","));

            if (isLikelyDn)
            {
                _logger.LogDebug($"[OU_DEBUG_BUILD] ouValueFromCsv '{ouValueFromCsv}' semble être un DN. Extraction des composants OU et DC.");
                var components = ouValueFromCsv.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => s.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || 
                                s.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (components.Any())
                {
                    string extractedOuPath = string.Join(",", components);
                    _logger.LogDebug($"[OU_DEBUG_BUILD] Chemin d'OU extrait du DN: '{extractedOuPath}'");
                    return extractedOuPath;
                }
                else
                {
                    _logger.LogWarning($"[OU_DEBUG_BUILD] Aucun composant OU ou DC trouvé dans le DN présumé '{ouValueFromCsv}'. Retour de defaultOu.");
                    return cleanDefaultOu;
                }
            }
            else // ouValueFromCsv est supposé être un nom simple ou un chemin relatif comme "Ventes/Europe"
            {
                var ouParts = ouValueFromCsv.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => $"OU={part.Trim()}")
                    .Reverse(); 

                string formattedOuPathRelative = string.Join(",", ouParts);

                if (string.IsNullOrEmpty(formattedOuPathRelative))
                {
                    _logger.LogWarning($"[OU_DEBUG_BUILD] ouValueFromCsv '{ouValueFromCsv}' n'a pas produit de parties d'OU valides après parsing. Retour de defaultOu.");
                    return cleanDefaultOu;
                }

                if (string.IsNullOrEmpty(cleanDefaultOu))
                {
                    _logger.LogDebug($"[OU_DEBUG_BUILD] defaultOu est vide. Retour de l'OU formatée relative: '{formattedOuPathRelative}'");
                    return formattedOuPathRelative;
                }
                else
                {
                    string finalPath = $"{formattedOuPathRelative},{cleanDefaultOu}";
                    _logger.LogDebug($"[OU_DEBUG_BUILD] defaultOu n'est pas vide. Concaténation pour chemin final: '{finalPath}'");
                    return finalPath;
                }
            }
        }

        private void CreateOuActions(List<string> uniqueOuValuesFromCsv, HashSet<string> existingOuDns, ImportConfig config, ImportAnalysis analysis)
        {
            _logger.LogDebug($"[OU_DEBUG] Début de CreateOuActions. uniqueOuValuesFromCsv: {uniqueOuValuesFromCsv.Count}, existingOuDns: {existingOuDns.Count}");
            foreach (var ouValueCsv in uniqueOuValuesFromCsv)
            {
                string ouPathTarget = BuildOuPath(ouValueCsv, config.DefaultOU); 
                
                _logger.LogInformation($"[OU_DEBUG_ACTION] Traitement de la valeur OU (normalisée) du CSV: '{ouValueCsv}'. DN cible construit: '{ouPathTarget}'.");

                if (!existingOuDns.Contains(ouPathTarget))
                {
                    string objectNameForAction = ExtractOuName(ouValueCsv);
                    _logger.LogInformation($"[OU_DEBUG_ACTION] => Action CREATE_OU sera ajoutée pour DN: '{ouPathTarget}' (ObjectName: '{objectNameForAction}' basé sur la valeur CSV '{ouValueCsv}')");
                    analysis.Actions.Add(new ImportAction
                    {
                        ActionType = ActionType.CREATE_OU,
                        ObjectName = objectNameForAction, 
                        Path = ouPathTarget,
                        Message = $"Création de l'unité organisationnelle '{objectNameForAction}' sous '{ExtractParentDnFromPath(ouPathTarget)}'",
                        Attributes = new Dictionary<string, string>()
                    });
                }
                else
                {
                     _logger.LogInformation($"[OU_DEBUG_ACTION] L'OU avec DN cible '{ouPathTarget}' (depuis valeur CSV '{ouValueCsv}') existe déjà (selon existingOuDns). Aucune action de création.");
                }
            }
        }
        
        private string ExtractParentDnFromPath(string ouPath)
        {
            if (string.IsNullOrEmpty(ouPath)) return string.Empty;
            int firstComma = ouPath.IndexOf(',');
            if (firstComma == -1 || firstComma == ouPath.Length - 1) return "racine du domaine";
            return ouPath.Substring(firstComma + 1).Trim();
        }

        private async Task ProcessUsersAsync(List<Dictionary<string, string>> csvData, ImportConfig config, ImportAnalysis analysis)
        {
            var ousToBeCreated = new HashSet<string>(
                analysis.Actions.Where(a => a.ActionType == ActionType.CREATE_OU).Select(a => a.Path),
                StringComparer.OrdinalIgnoreCase
            );
            var knownExistingOuPathsCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Cache pour les OUs dont l'existence est confirmée

            foreach (var row in csvData)
                await ProcessUserRowAsync(row, config, analysis, ousToBeCreated, knownExistingOuPathsCache); // Passer le nouveau cache
        }

        private async Task ProcessUserRowAsync(Dictionary<string, string> row, ImportConfig config, ImportAnalysis analysis, HashSet<string> ousToBeCreated, HashSet<string> knownExistingOuPathsCache)
        {
            var mappedRow = MapRow(row, config);
            string? samAccountName = mappedRow.GetValueOrDefault("sAMAccountName");
            if (string.IsNullOrEmpty(samAccountName))
            {
                analysis.Actions.Add(new ImportAction
                {
                    ActionType = ActionType.ERROR,
                    ObjectName = "Unknown",
                    Path = config.DefaultOU,
                    Message = "samAccountName manquant dans les données mappées",
                    Attributes = row
                });
                return;
            }
            
            string ouPath = DetermineUserOuPath(row, config);
            bool ouExists = false;
            bool existenceDetermined = false;

            if (string.IsNullOrEmpty(ouPath))
            {
                ouExists = true; // L'utilisateur sera dans l'OU par défaut (ou racine si DefaultOU est vide)
                existenceDetermined = true;
            }
            else
            {
                // 1. L'OU est-elle déjà planifiée pour création ? Si oui, elle "existera" pour cet utilisateur.
                if (ousToBeCreated.Contains(ouPath))
                {
                    ouExists = true;
                    existenceDetermined = true;
                    _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' planifiée pour création. Utilisateur: '{samAccountName}'.");
                }
                // 2. Sinon, l'OU est-elle dans notre cache d'OUs déjà vérifiées comme existantes ?
                else if (knownExistingOuPathsCache.Contains(ouPath))
                {
                    ouExists = true;
                    existenceDetermined = true;
                    _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' trouvée dans cache existance. Utilisateur: '{samAccountName}'.");
                }
            }

            if (!existenceDetermined && !string.IsNullOrEmpty(ouPath)) // Si non encore déterminée et ouPath valide, vérifier dans l'AD
            {
                _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' ni planifiée, ni en cache existance. Vérification AD. Utilisateur: '{samAccountName}'.");
                ouExists = await CheckOrganizationalUnitExistsAsync(ouPath);
                if (ouExists)
                {
                    knownExistingOuPathsCache.Add(ouPath); // Ajouter au cache si elle existe
                    _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' existe dans l'AD (ajoutée au cache).");
                }
                else
                {
                    _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' N'EXISTE PAS dans l'AD.");
                }
            }

            if (!ouExists && config.CreateMissingOUs && !string.IsNullOrEmpty(ouPath))
            {
                // Remplacer la vérification existante :
                // bool createOuActionAlreadyExists = analysis.Actions.Any(a => 
                // a.ActionType == ActionType.CREATE_OU && 
                // string.Equals(a.Path, ouPath, StringComparison.OrdinalIgnoreCase));
                // par une vérification directe dans ousToBeCreated:
                if (!ousToBeCreated.Contains(ouPath))
                {
                    string objectNameForOuAction = ExtractOuName(ouPath); 
                    _logger.LogInformation($"[USER_PROCESS] Ajout de l'action CREATE_OU pour l'OU manquante: '{ouPath}' (ObjectName: {objectNameForOuAction}) car non trouvée dans ousToBeCreated, requise par l'utilisateur '{samAccountName}'.");
                    analysis.Actions.Add(new ImportAction
                    {
                        ActionType = ActionType.CREATE_OU,
                        ObjectName = objectNameForOuAction,
                        Path = ouPath,
                        Message = $"Création de l'unité organisationnelle '{objectNameForOuAction}' (requise pour l'utilisateur '{samAccountName}')",
                        Attributes = new Dictionary<string, string>()
                    });
                    ousToBeCreated.Add(ouPath); // S'assurer que l'OU est marquée comme planifiée pour création
                }
                else
                {
                    _logger.LogInformation($"[USER_PROCESS] Action CREATE_OU pour '{ouPath}' déjà dans ousToBeCreated. Non ajoutée à nouveau par le traitement de l'utilisateur '{samAccountName}'.");
                }
                ouExists = true; 
            }

            if (!ouExists)
            {
                _logger.LogWarning($"L'OU '{ouPath}' n'existe pas et ne sera pas créée. Utilisation de l'OU par défaut pour l'utilisateur '{samAccountName}'");
                ouPath = config.DefaultOU;
            }
            
            samAccountName = samAccountName.Split('(')[0].Trim();

            var userExists = await _ldapService.UserExistsAsync(samAccountName);
            if (!userExists)
            {
                analysis.Actions.Add(new ImportAction
                {
                    ActionType = ActionType.CREATE_USER,
                    ObjectName = samAccountName,
                    Path = ouPath,
                    Message = "Création d'un nouvel utilisateur",
                    Attributes = mappedRow
                });
            }
            else
            {
                analysis.Actions.Add(new ImportAction
                {
                    ActionType = ActionType.UPDATE_USER,
                    ObjectName = samAccountName,
                    Path = ouPath,
                    Message = "Mise à jour d'un utilisateur existant",
                    Attributes = mappedRow
                });
            }
        }

        private string DetermineUserOuPath(Dictionary<string, string> row, ImportConfig config)
        {
            if (string.IsNullOrEmpty(config.ouColumn) || !row.ContainsKey(config.ouColumn) || string.IsNullOrEmpty(row[config.ouColumn]))
                return config.DefaultOU;
            string ouValue = row[config.ouColumn];
            return BuildOuPath(ouValue, config.DefaultOU);
        }

        private void UpdateAnalysisSummary(ImportAnalysis analysis)
        {
            analysis.Summary.CreateCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_USER);
            analysis.Summary.UpdateCount = analysis.Actions.Count(a => a.ActionType == ActionType.UPDATE_USER);
            analysis.Summary.DeleteCount = analysis.Actions.Count(a => a.ActionType == ActionType.DELETE_USER);
            analysis.Summary.ErrorCount = analysis.Actions.Count(a => a.ActionType == ActionType.ERROR);
            analysis.Summary.CreateOUCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_OU);
            analysis.Summary.DeleteOUCount = analysis.Actions.Count(a => a.ActionType == ActionType.DELETE_OU);
            analysis.Summary.MoveCount = analysis.Actions.Count(a => a.ActionType == ActionType.MOVE_USER);
        }

        #endregion

        #region Import du CSV

        public async Task<ImportResult> ExecuteImportFromDataAsync(List<Dictionary<string, string>> csvData, ImportConfig config, string? connectionId = null)
        {
            var sw = Stopwatch.StartNew();
            _logger.LogInformation("Début de l'import depuis les données CSV");

            try
            {
                config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
                var analysis = await AnalyzeCsvDataForActionsAsync(csvData, config);

                if (analysis == null || analysis.Actions == null || !analysis.Actions.Any())
                {
                    _logger.LogWarning("Aucune action générée pour l'import");
                    return new ImportResult
                    {
                        Success = false,
                        Summary = new ImportSummary { TotalObjects = csvData.Count },
                        ActionResults = new List<ImportActionResult>
                        { 
                            new ImportActionResult 
                            { 
                                Success = false, 
                                Message = "Aucune action générée pour l'import."
                            }
                        },
                        Details = "Aucune action générée pour l'import."
                    };
                }

                return await ExecuteImport(analysis.Actions, config, connectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de l'import depuis les données CSV");
                return new ImportResult
                {
                    Success = false,
                    ActionResults = new List<ImportActionResult>
                    { 
                        new ImportActionResult 
                        { 
                            Success = false, 
                            Message = $"Erreur lors de l'import: {ex.Message}"
                        }
                    },
                    Details = $"Erreur lors de l'import: {ex.Message}",
                    Summary = new ImportSummary { ErrorCount = 1 }
                };
            }
            finally
            {
                sw.Stop();
                _logger.LogInformation($"Import terminé en {sw.ElapsedMilliseconds} ms");
            }
        }
        
        public async Task<ImportResult> ExecuteImportFromActionsAsync(List<Dictionary<string, string>> csvData, ImportConfig config, List<LegacyImportActionItem> actions, string? connectionId = null)
        {
            _logger.LogInformation("Début de l'exécution de l'import à partir des actions");
            
            var result = new ImportResult
            {
                Success = true,
                TotalProcessed = actions.Count,
                TotalSucceeded = 0,
                TotalFailed = 0,
                CreatedCount = 0,
                UpdatedCount = 0,
                DeletedCount = 0,
                MovedCount = 0,
                ErrorCount = 0,
                Summary = new ImportSummary 
                { 
                    TotalObjects = actions.Count,
                    CreateCount = 0,
                    UpdateCount = 0,
                    DeleteCount = 0,
                    MoveCount = 0,
                    ErrorCount = 0,
                    CreateOUCount = 0
                },
                ActionResults = new List<ImportActionResult>()
            };
            
            try
            {
                // Calculer les statistiques basées sur les types d'action
                int createCount = 0;
                int updateCount = 0;
                int errorCount = 0;
                int ouCreateCount = 0;
                int successCount = 0;
                int failedCount = 0;
                
                // Convertir les actions LegacyImportActionItem en ImportAction pour utiliser la méthode ExecuteImport
                var importActions = new List<ImportAction>();
                foreach (var action in actions)
                {
                    if (!action.IsValid)
                    {
                        errorCount++;
                        failedCount++;
                        continue;
                    }
                    
                    if (Enum.TryParse<ActionType>(action.ActionType, out var actionType))
                    {
                        var importAction = new ImportAction
                        {
                            ActionType = actionType,
                            ObjectName = action.Data.ContainsKey("objectName") ? action.Data["objectName"] : "Unknown",
                            Path = action.Data.ContainsKey("path") ? action.Data["path"] : "",
                            Message = action.Data.ContainsKey("message") ? action.Data["message"] : "Action exécutée",
                            Attributes = new Dictionary<string, string>(action.Data)
                        };
                        
                        importActions.Add(importAction);
                    }
                    else
                    {
                        errorCount++;
                        failedCount++;
                    }
                }
                
                // Si nous avons des actions valides, exécuter l'import
                if (importActions.Any())
                {
                    return await ExecuteImport(importActions, config, connectionId);
                }
                else
                {
                    // Aucune action valide à exécuter
                    result.Success = false;
                    result.TotalProcessed = actions.Count;
                    result.TotalSucceeded = 0;
                    result.TotalFailed = actions.Count;
                    result.ErrorCount = actions.Count;
                    result.Summary.ErrorCount = actions.Count;
                    result.ActionResults.Add(new ImportActionResult
                    {
                        ActionType = ActionType.ERROR,
                        Message = "Aucune action valide à exécuter",
                        Success = false
                    });
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de l'exécution de l'import à partir des actions");
                result.Success = false;
                result.TotalFailed = actions.Count;
                result.ErrorCount++;
                result.Summary.ErrorCount++;
                result.ActionResults.Add(new ImportActionResult
                {
                    ActionType = ActionType.ERROR,
                    Message = $"Erreur lors de l'import: {ex.Message}",
                    Success = false
                });
                return result;
            }
        }

        public async Task<ImportResult> ExecuteImport(List<ImportAction> actions, ImportConfig config, string? connectionId = null)
        {
            var sw = Stopwatch.StartNew();
            _logger.LogInformation($"Début de l'exécution de l'import - {actions.Count} actions à traiter");

            var result = new ImportResult
            {
                Success = true,
                Summary = new ImportSummary { TotalObjects = actions.Count },
                ActionResults = new List<ImportActionResult>(),
                Details = "Import exécuté avec succès"
            };

            try
            {
                // Récupérer une instance du service SignalR si un ID de connexion est fourni
                ISignalRService signalRService = null;
                if (!string.IsNullOrEmpty(connectionId))
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    signalRService = scope.ServiceProvider.GetRequiredService<ISignalRService>();
                }

                // Définir l'ordre de traitement des actions
                var actionProcessingOrder = new List<ActionType>
                {
                    ActionType.CREATE_OU,
                    ActionType.CREATE_USER,
                    ActionType.UPDATE_USER,
                    ActionType.MOVE_USER, // Même si non implémenté, garder sa place logique
                    ActionType.DELETE_USER,
                    ActionType.DELETE_OU // Traiter la suppression des OUs en dernier
                };

                int totalActions = actions.Count;
                int processedActions = 0;
                
                // Traiter les actions selon l'ordre défini
                foreach (var actionTypeToProcess in actionProcessingOrder)
                {
                    if (actions.All(a => a.ActionType != actionTypeToProcess))
                        continue;

                    List<ImportAction> actionsOfType = actions.Where(a => a.ActionType == actionTypeToProcess).ToList();
                    _logger.LogInformation($"Traitement de {actionsOfType.Count} actions de type {actionTypeToProcess}.");

                    // Traitement séquentiel pour CREATE_OU et DELETE_OU
                    if (actionTypeToProcess == ActionType.CREATE_OU || actionTypeToProcess == ActionType.DELETE_OU)
                    {
                        foreach (var action in actionsOfType)
                        {
                            var actionResult = await ExecuteImportActionAsync(action, result);
                            result.ActionResults.Add(actionResult);
                            processedActions++;
                            UpdateCountsAndSendProgress(result, actionResult, action, processedActions, totalActions, signalRService, connectionId, actionTypeToProcess.ToString().ToLowerInvariant());
                        }
                    }
                    else // Traitement potentiellement parallèle pour les actions utilisateur
                    {
                        // Limiter le degré de parallélisme
                        int maxParallelism = Math.Min(Environment.ProcessorCount, 4);
                        object progressLock = new object();

                        await Parallel.ForEachAsync(
                            actionsOfType,
                            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                            async (action, token) =>
                            {
                                var actionResult = await ExecuteImportActionAsync(action, result);
                                lock (progressLock)
                                {
                                    result.ActionResults.Add(actionResult);
                                    processedActions++;
                                    UpdateCountsAndSendProgress(result, actionResult, action, processedActions, totalActions, signalRService, connectionId, "processing_users");
                                }
                            }
                        );
                    }
                }

                // Vérifier s'il y a eu des erreurs (à l'extérieur des boucles parallèles)
                result.Success = result.ActionResults.All(ar => ar.Success); // Basé sur le succès de toutes les actions individuelles
                
                // Mettre à jour le résumé final en se basant sur ActionResults pour tous les compteurs
                result.Summary.CreateCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_USER && ar.Success);
                result.Summary.UpdateCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.UPDATE_USER && ar.Success);
                result.Summary.DeleteCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.DELETE_USER && ar.Success);
                result.Summary.MoveCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.MOVE_USER && ar.Success); // Même si non pleinement implémenté, le comptage est prêt
                result.Summary.ErrorCount = result.ActionResults.Count(ar => !ar.Success);
                result.Summary.CreateOUCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_OU && ar.Success);
                result.Summary.DeleteOUCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.DELETE_OU && ar.Success);
                
                result.Summary.TotalObjects = actions.Count; // Nombre total d'actions planifiées
                result.Summary.ProcessedCount = processedActions; // Nombre d'actions effectivement tentées

                // Conserver les valeurs sur l'objet result principal pour la cohérence si utilisées ailleurs directement
                result.CreatedCount = result.Summary.CreateCount;
                result.UpdatedCount = result.Summary.UpdateCount;
                result.DeletedCount = result.Summary.DeleteCount;
                result.MovedCount = result.Summary.MoveCount;
                result.ErrorCount = result.Summary.ErrorCount;

                // Envoyer la notification de fin de traitement
                if (signalRService != null)
                {
                    await signalRService.SendCsvAnalysisProgressAsync(
                        connectionId,
                        100,
                        result.Success ? "completed" : "completed_with_errors",
                        $"Import terminé avec {result.Summary.CreateCount} créations, {result.Summary.UpdateCount} mises à jour, {result.Summary.DeleteCount} suppressions et {result.Summary.ErrorCount} erreurs",
                        new ImportAnalysis { Summary = result.Summary }
                    );
                }

                sw.Stop();
                _logger.LogInformation($"Import terminé en {sw.ElapsedMilliseconds} ms");
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Erreur lors de l'exécution de l'import");
                
                result.Success = false;
                result.Details = $"Erreur lors de l'import: {ex.Message}";
                
                if (result.Summary == null)
                    result.Summary = new ImportSummary { TotalObjects = actions.Count, ErrorCount = 1 };
                else
                    result.Summary.ErrorCount++;
                
                return result;
            }
        }

        // Nouvelle méthode pour centraliser la mise à jour des compteurs et l'envoi de la progression
        private void UpdateCountsAndSendProgress(ImportResult currentImportResult, ImportActionResult actionResult, ImportAction action, int processedCount, int totalCount, ISignalRService signalRService, string? connectionId, string currentPhase)
        {
            if (!actionResult.Success)
            {
                currentImportResult.ErrorCount++;
            }
            else
            {
                switch (action.ActionType)
                {
                    case ActionType.CREATE_USER:
                        currentImportResult.CreatedCount++;
                        break;
                    case ActionType.UPDATE_USER:
                        currentImportResult.UpdatedCount++;
                        break;
                    case ActionType.DELETE_USER:
                        currentImportResult.DeletedCount++;
                        break;
                    case ActionType.MOVE_USER:
                        currentImportResult.MovedCount++;
                        break;
                    case ActionType.CREATE_OU:
                        // CreateOUCount est déjà géré dans le résumé final, ici on ne met à jour que le Summary du ImportResult global si besoin
                        break;
                    case ActionType.DELETE_OU:
                        // DeleteOUCount sera aussi géré dans le résumé final
                        break;
                }
            }

            if (signalRService != null && !string.IsNullOrEmpty(connectionId))
            {
                int progressPercentage = (int)((processedCount * 100.0) / totalCount);
                string message = $"Traitement: {action.ActionType} pour {action.ObjectName} - {processedCount}/{totalCount}";

                var currentAnalysisForProgress = new ImportAnalysis
                {
                    Summary = new ImportSummary
                    {
                        TotalObjects = totalCount,
                        CreateCount = currentImportResult.CreatedCount,
                        UpdateCount = currentImportResult.UpdatedCount,
                        DeleteCount = currentImportResult.DeletedCount,
                        MoveCount = currentImportResult.MovedCount,
                        ErrorCount = currentImportResult.ErrorCount,
                        CreateOUCount = currentImportResult.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_OU && ar.Success), // Recalculer pour la progression
                        DeleteOUCount = currentImportResult.ActionResults.Count(ar => ar.ActionType == ActionType.DELETE_OU && ar.Success),
                        ProcessedCount = processedCount
                    }
                };

                signalRService.SendCsvAnalysisProgressAsync(
                    connectionId,
                    progressPercentage,
                    currentPhase,
                    message,
                    currentAnalysisForProgress
                ).GetAwaiter().GetResult(); // .Wait() ou .GetAwaiter().GetResult() dans un contexte non-async
            }
        }

        private async Task<ImportActionResult> ExecuteImportActionAsync(ImportAction action, ImportResult result)
        {
            var actionResult = new ImportActionResult
            {
                ActionType = action.ActionType,
                Success = false,
                ObjectName = action.ObjectName,
                Path = action.Path,
                Message = $"Erreur lors de l'exécution de l'action {action.ActionType} pour {action.ObjectName}"
            };

            try
            {
                switch (action.ActionType)
                {
                    case ActionType.CREATE_OU:
                        await Task.Run(() => _ldapService.CreateOrganizationalUnit(action.Path));
                        actionResult.Success = true;
                        actionResult.Message = $"Création de {action.ObjectName} ({action.Path}) effectuée";
                        break;
                    case ActionType.CREATE_USER:
                        var userAttributes = new Dictionary<string, string>(action.Attributes);
                        if (userAttributes.ContainsKey("path"))
                            userAttributes.Remove("path");
                        
                        // Vérification des attributs requis
                        var hasGivenName = userAttributes.ContainsKey("givenName") && !string.IsNullOrEmpty(userAttributes["givenName"]);
                        var hasSn = userAttributes.ContainsKey("sn") && !string.IsNullOrEmpty(userAttributes["sn"]);
                        var hasPassword = userAttributes.ContainsKey("password") || userAttributes.ContainsKey("userPassword");
                        
                        if (!hasGivenName || !hasSn || !hasPassword)
                        {
                            // Ajouter un mot de passe par défaut si absent
                            if (!hasPassword)
                            {
                                string defaultPassword = "Changeme1!";
                                userAttributes["password"] = defaultPassword;
                                _logger.LogWarning($"Ajout d'un mot de passe par défaut '{defaultPassword}' pour {action.ObjectName}");
                            }
                            
                            // Dériver givenName et sn du samAccountName si nécessaire
                            if (!hasGivenName || !hasSn)
                            {
                                string sam = action.ObjectName;
                                var parts = sam.Split('.');
                                
                                if (parts.Length >= 2)
                                {
                                    if (!hasGivenName)
                                    {
                                        userAttributes["givenName"] = char.ToUpper(parts[0][0]) + parts[0].Substring(1);
                                        _logger.LogWarning($"Dérivation de givenName '{userAttributes["givenName"]}' depuis sAMAccountName '{sam}'");
                                    }
                                    
                                    if (!hasSn)
                                    {
                                        userAttributes["sn"] = char.ToUpper(parts[1][0]) + parts[1].Substring(1);
                                        _logger.LogWarning($"Dérivation de sn '{userAttributes["sn"]}' depuis sAMAccountName '{sam}'");
                                    }
                                }
                                else
                                {
                                    // Si on ne peut pas dériver du samAccountName, utilisez des valeurs par défaut
                                    if (!hasGivenName)
                                    {
                                        userAttributes["givenName"] = sam;
                                        _logger.LogWarning($"Utilisation de samAccountName comme givenName pour {sam}");
                                    }
                                    
                                    if (!hasSn)
                                    {
                                        userAttributes["sn"] = "User";
                                        _logger.LogWarning($"Utilisation de 'User' comme sn pour {sam}");
                                    }
                                }
                            }
                        }
                        
                        _ldapService.CreateUser(action.ObjectName, userAttributes, action.Path);
                        actionResult.Success = true;
                        actionResult.Message = $"Création de {action.ObjectName} dans l\'OU {action.Path} effectuée";
                        break;
                    case ActionType.UPDATE_USER:
                        //TODO : A FAIRE PLUS TARD !!!!
                        /*var updateAttributes = new Dictionary<string, string>(action.Attributes)
                        {
                            ["path"] = action.Path
                        };
                        await Task.Run(() => _ldapService.UpdateUser(action.ObjectName, updateAttributes));
                        actionResult.Success = true;
                        actionResult.Message = $"Mise à jour de {action.ObjectName} effectuée";*/
                        break;
                    case ActionType.DELETE_USER:
                        await Task.Run(() => _ldapService.DeleteUserAsync(action.ObjectName, action.Path)); 
                        actionResult.Success = true;
                        actionResult.Message = $"Suppression de {action.ObjectName} effectuée";
                        break;
                    case ActionType.MOVE_USER:
                        actionResult.Message = $"Déplacement de {action.ObjectName} vers {action.Path} effectué (NON IMPLEMENTE ACTUELLEMENT)";
                        _logger.LogWarning("L'action MOVE_USER n'est pas encore pleinement implémentée dans ExecuteImportActionAsync.");
                        break;
                    case ActionType.DELETE_OU:
                        await Task.Run(() => _ldapService.DeleteOrganizationalUnitAsync(action.Path));
                        actionResult.Message = $"Suppression de l'unité organisationnelle {action.ObjectName} ({action.Path}) effectuée";
                        break;
                    default:
                        actionResult.Success = false;
                        actionResult.Message = "Type d\'action non supporté";
                        break;
                }
            }
            catch (Exception ex)
            {
                actionResult.Success = false;
                actionResult.Message = $"Erreur: {ex.Message}\nStackTrace: {ex.StackTrace}";
                _logger.LogError(ex, $"Erreur lors de l\'exécution de l\'action {action.ActionType} pour {action.ObjectName}");
            }

            return actionResult;
        }

        public async Task<List<OrganizationalUnitModel>> GetAllOrganizationalUnitsAsync()
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var ldapService = scope.ServiceProvider.GetRequiredService<ILdapService>();
                return await ldapService.GetAllOrganizationalUnitsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération des unités organisationnelles");
                return new List<OrganizationalUnitModel>();
            }
        }

        public async Task<ImportResult> ProcessCsvDataAsync(List<Dictionary<string, string>> data, ImportConfig config)
        {
            _logger.LogInformation($"Traitement des données CSV: {data.Count} lignes");
            
            // Analyser d'abord les données
            var analysisResult = await AnalyzeCsvDataAsync(data, config);
            
            // Créer une liste d'actions vide
            var actions = new List<LegacyImportActionItem>();
            
            // Si l'analyse est valide, déterminer automatiquement les actions
            if (analysisResult.IsValid)
            {
                // Logique pour déterminer les actions basée sur l'analyse
                // Pour cet exemple, nous supposons que c'est implémenté ailleurs
            }
            
            // Exécuter les actions
            return await ExecuteImportFromActionsAsync(data, config, actions);
        }

        #endregion

        #region Méthodes utilitaires

        /// <summary>
        /// Transforme une ligne CSV en dictionnaire d'attributs AD en utilisant un mapping avec template.
        /// Pour chaque entrée du mapping, la clé représente l'attribut AD et la valeur correspond au template.
        /// Les tokens de la forme %nomDeColonne% ou %nomDeColonne:modifier% sont remplacés par la valeur correspondante dans la ligne CSV.
        /// </summary>
        private Dictionary<string, string> MapRow(Dictionary<string, string> row, ImportConfig config)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _logger.LogDebug($"Début du mappage de la ligne CSV. Clés CSV présentes: {string.Join(", ", row.Keys)}");
            _logger.LogDebug($"Configuration de mapping: {JsonSerializer.Serialize(config.HeaderMapping)}");

            foreach (var mapping in config.HeaderMapping)
            {
                string adAttribute = mapping.Key;
                string template = mapping.Value;

                if (string.IsNullOrEmpty(template))
                {
                    _logger.LogDebug($"Mapping pour l'attribut AD '{adAttribute}' ignoré car le template est vide.");
                    continue;
                }

                _logger.LogDebug($"Traitement du mapping pour l'attribut AD '{adAttribute}' avec le template: '{template}'.");
                string finalValue = ApplyTemplate(template, row);
                if (!string.IsNullOrEmpty(finalValue))
                {
                    _logger.LogDebug($"Attribut AD '{adAttribute}': template '{template}' a produit la valeur '{finalValue}'. Normalisation en cours.");
                    result[adAttribute] = NormalizeAdAttribute(adAttribute, finalValue);
                    _logger.LogDebug($"Attribut AD '{adAttribute}' mappé à '{result[adAttribute]}' (après normalisation).");
                }
                else
                {
                    _logger.LogDebug($"Aucune valeur produite pour l'attribut AD '{adAttribute}' avec le template '{template}' pour la ligne CSV en cours.");
                }
            }
            
            // Vérification des attributs requis
            bool hasGivenName = result.ContainsKey("givenName") && !string.IsNullOrEmpty(result["givenName"]);
            bool hasSn = result.ContainsKey("sn") && !string.IsNullOrEmpty(result["sn"]);
            bool hasPassword = result.ContainsKey("password") || result.ContainsKey("userPassword");
            
            _logger.LogInformation($"Vérification des attributs requis pour la ligne: givenName={hasGivenName}, sn={hasSn}, password={hasPassword}");
            
            if (!hasGivenName || !hasSn || !hasPassword)
            {
                // Journaliser la ligne complète pour faciliter le débogage
                _logger.LogWarning($"Ligne CSV avec attributs manquants: {JsonSerializer.Serialize(row)}");
                _logger.LogWarning($"Résultat du mapping avec attributs manquants: {JsonSerializer.Serialize(result)}");
                
                // Ajouter un mot de passe par défaut si absent
                if (!hasPassword)
                {
                    string defaultPassword = "Changeme1!";
                    result["password"] = defaultPassword;
                    _logger.LogWarning($"Ajout d'un mot de passe par défaut '{defaultPassword}' pour la création d'utilisateur");
                }
                
                // Dériver givenName et sn du samAccountName si nécessaire
                if (result.TryGetValue("sAMAccountName", out var sam) && !string.IsNullOrEmpty(sam))
                {
                    var parts = sam.Split('.');
                    if (parts.Length >= 2 && !hasGivenName)
                    {
                        result["givenName"] = char.ToUpper(parts[0][0]) + parts[0].Substring(1);
                        _logger.LogWarning($"Dérivation de givenName '{result["givenName"]}' depuis sAMAccountName '{sam}'");
                    }
                    
                    if (parts.Length >= 2 && !hasSn)
                    {
                        result["sn"] = char.ToUpper(parts[1][0]) + parts[1].Substring(1);
                        _logger.LogWarning($"Dérivation de sn '{result["sn"]}' depuis sAMAccountName '{sam}'");
                    }
                }
            }
            
            _logger.LogDebug($"Fin du mappage de la ligne CSV. Attributs AD mappés: {JsonSerializer.Serialize(result)}");
            return result;
        }

        /// <summary>
        /// Remplace dans le template les tokens de la forme %nomDeColonne% ou %nomDeColonne:modifier%
        /// par la valeur correspondante dans la ligne CSV.
        /// Les modificateurs supportés sont :
        /// - uppercase : conversion en majuscules
        /// - lowercase : conversion en minuscules
        /// - first : première lettre uniquement
        /// </summary>
        private string ApplyTemplate(string template, Dictionary<string, string> row)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            var regex = new Regex("%([^%:]+)(?::([^%]+))?%");
            string result = regex.Replace(template, match =>
            {
                string column = match.Groups[1].Value.Trim();
                string? modifier = match.Groups[2].Success ? match.Groups[2].Value.Trim().ToLowerInvariant() : null;

                var key = row.Keys.FirstOrDefault(k => string.Equals(k.Trim(), column, StringComparison.OrdinalIgnoreCase));
                if (key != null && row.TryGetValue(key, out var value))
                {
                    if (!string.IsNullOrEmpty(modifier))
                    {
                        switch (modifier)
                        {
                            case "uppercase":
                                return value.ToUpperInvariant();
                            case "lowercase":
                                return value.ToLowerInvariant();
                            case "first":
                                return value.Length > 0 ? value[0].ToString() : "";
                            default:
                                return value;
                        }
                    }
                    return value;
                }
                return "";
            });

            return result.Trim();
        }

        /// <summary>
        /// Normalise la valeur d'un attribut AD en fonction du type d'attribut.
        /// - "mail" et "userPrincipalName" seront convertis en minuscules.
        /// - "samAccountName" sera nettoyé (suppression d'accents, conversion en minuscules, suppression d'espaces).
        /// - "displayName" sera mis en title case.
        /// </summary>
        private string NormalizeAdAttribute(string attributeName, string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            value = value.Trim();

            switch (attributeName.ToLowerInvariant())
            {
                case "mail":
                case "userprincipalname":
                    return value.ToLowerInvariant();
                case "samaccountname":
                    return RemoveDiacritics(value).ToLowerInvariant().Replace(" ", "");
                case "displayname":
                    return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
                default:
                    return RemoveDiacritics(value);
            }
        }

        /// <summary>
        /// Supprime les diacritiques (accents) d'une chaîne.
        /// </summary>
        private string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();
            foreach (var c in normalizedString)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    stringBuilder.Append(c);
            }
            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Délègue la vérification d'existence d'une OU au service LDAP.
        /// </summary>
        private async Task<bool> CheckOrganizationalUnitExistsAsync(string ouPath)
        {
            return await _ldapService.OrganizationalUnitExistsAsync(ouPath);
        }

        private ISpreadsheetParserService ChooseParser(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            _logger.LogDebug($"Choix du parser pour l'extension de fichier: {ext}");

            ISpreadsheetParserService selectedParser = null;

            switch (ext)
            {
                case ".csv":
                    // Tenter de trouver un parser qui se déclare comme CsvParserService
                    // ou un nom/type identifiable si vous avez plusieurs parsers CSV.
                    selectedParser = _parsers.FirstOrDefault(p => p.GetType().Name.Contains("CsvParser", StringComparison.OrdinalIgnoreCase));
                    if (selectedParser == null)
                    {
                        _logger.LogWarning("Aucun parser CSV spécifique (contenant CsvParser dans son nom) n'a été trouvé. Tentative avec le premier parser disponible.");
                        selectedParser = _parsers.FirstOrDefault(); // Fallback au premier parser si aucun spécifique n'est trouvé
                    }
                    break;
                case ".xlsx":
                case ".xls": // Ajout pour gérer les anciens formats Excel également
                    // Tenter de trouver un parser qui se déclare comme ExcelParserService
                    selectedParser = _parsers.FirstOrDefault(p => p.GetType().Name.Contains("ExcelParser", StringComparison.OrdinalIgnoreCase));
                     if (selectedParser == null)
                    {
                        _logger.LogWarning($"Aucun parser Excel spécifique (contenant ExcelParser dans son nom) n'a été trouvé pour {ext}. Tentative avec le premier parser disponible.");
                        selectedParser = _parsers.FirstOrDefault(); // Fallback
                    }
                    break;
                default:
                    _logger.LogError($"Format de fichier non supporté: {ext} pour le fichier {fileName}");
                    throw new NotSupportedException($"Le format de fichier '{ext}' n'est pas supporté.");
            }

            if (selectedParser == null)
            {
                _logger.LogError("Aucun parser approprié n'a pu être sélectionné ou aucun parser n'est enregistré.");
                throw new InvalidOperationException("Aucun parser approprié disponible pour traiter le fichier.");
            }
            
            _logger.LogInformation($"Parser sélectionné: {selectedParser.GetType().FullName} pour le fichier {fileName}");
            return selectedParser;
        }

        #endregion

        // Nouvelle section pour la gestion des utilisateurs orphelins
        #region Orphan User Cleanup

        private async Task<List<string>> ProcessOrphanedUsersAsync(List<Dictionary<string, string>> csvData, ImportConfig config, ImportAnalysis analysis)
        {
            string rootOuForCleanup = config.DefaultOU;
            List<string> finalOusToScan = new List<string>(); // Liste des OUs réellement scannées

            if (string.IsNullOrEmpty(rootOuForCleanup))
            {
                _logService.Log("IMPORT_ORPHANS_WARN", "[IMPORT ORPHANS] Nettoyage des orphelins activé, mais config.DefaultOU (OU racine pour le nettoyage) est vide. Le processus est annulé.");
                analysis.Actions.Add(new ImportAction {
                    ActionType = ActionType.ERROR, ObjectName = "Configuration Nettoyage Orphelins", Path = "",
                    Message = "Nettoyage des orphelins activé, mais l'OU racine pour le nettoyage (config.DefaultOU) n'est pas configurée.",
                    Attributes = new Dictionary<string, string>()
                });
                return finalOusToScan; // Retourner la liste vide
            }

            _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Début du processus de nettoyage des utilisateurs orphelins. OU racine pour le scan: {rootOuForCleanup}");

            // 1. Obtenir tous les sAMAccountNames des utilisateurs présents dans le fichier CSV (liste globale de référence)
            var allSamAccountNamesFromCsv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Collecte de tous les sAMAccountNames depuis le CSV pour la comparaison globale.");
            foreach (var rowInCsv in csvData)
            {
                var mappedAttributes = MapRow(rowInCsv, config);
                if (!mappedAttributes.TryGetValue("sAMAccountName", out var samAccountNameFromMapping) || string.IsNullOrEmpty(samAccountNameFromMapping))
                {
                    _logger.LogWarning($"[ORPHAN_DEBUG] sAMAccountName non trouvé/vide après mapping pour une ligne CSV. Attributs: {JsonSerializer.Serialize(mappedAttributes)}");
                    continue;
                }
                string cleanedSamForComparison = samAccountNameFromMapping.Split('(')[0].Trim();
                if (string.IsNullOrEmpty(cleanedSamForComparison))
                {
                     _logger.LogWarning($"[ORPHAN_DEBUG] sAMAccountName nettoyé est vide. Original mappé: '{samAccountNameFromMapping}'. Ignoré.");
                    continue;
                }
                allSamAccountNamesFromCsv.Add(cleanedSamForComparison);
            }
            _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] {allSamAccountNamesFromCsv.Count} sAMAccountNames uniques collectés depuis le CSV. Ex: {string.Join(", ", allSamAccountNamesFromCsv.Take(5))}{(allSamAccountNamesFromCsv.Count > 5 ? "..." : "")}");

            // 2. Obtenir la liste de toutes les OUs à scanner (l'OU racine et ses descendantes)
            // List<string> ousToScan; // Sera remplacée par finalOusToScan
            try
            {
                _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Récupération des OUs à scanner récursivement à partir de : {rootOuForCleanup}");
                finalOusToScan = await _ldapService.GetOrganizationalUnitPathsRecursiveAsync(rootOuForCleanup);
                if (!finalOusToScan.Any())
                {
                     _logService.Log("IMPORT_ORPHANS_WARN", $"[IMPORT ORPHANS] Aucune OU (même pas l'OU racine '{rootOuForCleanup}') n'a été trouvée pour le scan des orphelins. Le nettoyage est arrêté pour cette branche.");
                     return finalOusToScan; // Retourner la liste vide
                }
                 _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] {finalOusToScan.Count} OUs seront scannées pour les orphelins. Ex: {string.Join("; ", finalOusToScan.Take(3))}{(finalOusToScan.Count > 3 ? "..." : "")}");
            }
            catch (Exception ex)
            {
                _logService.LogError("IMPORT_ORPHANS_ERROR", $"[IMPORT ORPHANS] Erreur lors de la récupération de la liste des OUs à scanner à partir de {rootOuForCleanup}: {ex.Message}", ex);
                analysis.Actions.Add(new ImportAction {
                    ActionType = ActionType.ERROR, ObjectName = $"Erreur Listage OUs Récursif - {rootOuForCleanup}", Path = rootOuForCleanup,
                    Message = $"Impossible de lister les OUs sous {rootOuForCleanup} pour le nettoyage: {ex.Message}",
                    Attributes = new Dictionary<string, string>()
                });
                return finalOusToScan; // Retourner la liste potentiellement vide ou partiellement remplie
            }
            
            int totalOrphanedUsersFound = 0;

            // 3. Pour chaque OU à scanner, trouver et marquer les orphelins
            foreach (var currentOuToScan in finalOusToScan)
            {
                _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Scan de l'OU: '{currentOuToScan}' pour les utilisateurs orphelins.");
                List<string> existingSamAccountNamesInCurrentOU;
                try
                {
                    existingSamAccountNamesInCurrentOU = await _ldapService.GetUsersInOUAsync(currentOuToScan);
                    _logger.LogDebug($"[ORPHAN_DEBUG] Dans '{currentOuToScan}', utilisateurs AD trouvés: {existingSamAccountNamesInCurrentOU.Count}. Ex: {string.Join(", ", existingSamAccountNamesInCurrentOU.Take(5))}{(existingSamAccountNamesInCurrentOU.Count > 5 ? "..." : "")}");
                }
                catch (Exception ex)
                {
                    _logService.LogError("IMPORT_ORPHANS_ERROR", $"[IMPORT ORPHANS] Erreur lors de la récupération des utilisateurs de l'OU '{currentOuToScan}' pour le nettoyage: {ex.Message}", ex);
                    analysis.Actions.Add(new ImportAction {
                        ActionType = ActionType.ERROR, ObjectName = $"Erreur Listage Utilisateurs AD - {currentOuToScan}", Path = currentOuToScan,
                        Message = $"Impossible de lister les utilisateurs AD dans l'OU '{currentOuToScan}' pour le nettoyage: {ex.Message}",
                        Attributes = new Dictionary<string, string>()
                    });
                    continue; 
                }

                if (!existingSamAccountNamesInCurrentOU.Any())
                {
                    _logger.LogDebug($"[ORPHAN_DEBUG] Aucun utilisateur AD trouvé dans l'OU '{currentOuToScan}'. Passage à l'OU suivante.");
                    continue;
                }

                int orphanedInThisOU = 0;
                foreach (var existingSamInAD in existingSamAccountNamesInCurrentOU)
                {
                    if (!allSamAccountNamesFromCsv.Contains(existingSamInAD)) 
                    {
                        _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Utilisateur orphelin identifié: '{existingSamInAD}' dans l'OU '{currentOuToScan}'. Action de suppression sera ajoutée.");
                        analysis.Actions.Add(new ImportAction
                        {
                            ActionType = ActionType.DELETE_USER,
                            ObjectName = existingSamInAD,
                            Path = currentOuToScan, 
                            Message = $"Suppression de l'utilisateur orphelin '{existingSamInAD}' de l'OU '{currentOuToScan}' car non présent dans le fichier CSV actuel.",
                            Attributes = new Dictionary<string, string>()
                        });
                        orphanedInThisOU++;
                        totalOrphanedUsersFound++;
                    }
                }
                _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Dans l'OU '{currentOuToScan}', {orphanedInThisOU} utilisateurs orphelins identifiés.");
            }
            _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Processus de nettoyage terminé. Total de {totalOrphanedUsersFound} utilisateurs orphelins identifiés sur toutes les OUs scannées sous '{rootOuForCleanup}'.");
            return finalOusToScan; // Retourner la liste des OUs qui ont été scannées
        }
        #endregion

        #region Empty OU Cleanup
        private async Task ProcessEmptyOrganizationalUnitsAsync(List<string> scannedOuDns, ImportConfig config, ImportAnalysis analysis)
        {
            if (scannedOuDns == null || !scannedOuDns.Any())
            {
                _logService.Log("EMPTY_OU_CLEANUP_INFO", "[EMPTY OU] Aucune OU n'a été scannée pour les utilisateurs orphelins, donc pas de nettoyage d'OUs vides.");
                return;
            }

            _logService.Log("EMPTY_OU_CLEANUP_INFO", $"[EMPTY OU] Début du processus de vérification des OUs vides parmi les {scannedOuDns.Count} OUs scannées.");

            // Trier les OUs par longueur de DN, de la plus longue à la plus courte (enfants d'abord)
            // Exclure l'OU racine (config.DefaultOU) de la suppression automatique pour des raisons de sécurité.
            var candidateOusForDeletion = scannedOuDns
                .Where(ouDn => !string.Equals(ouDn, config.DefaultOU, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ouDn => ouDn.Length)
                .ToList();

            if (!candidateOusForDeletion.Any())
            {
                _logService.Log("EMPTY_OU_CLEANUP_INFO", "[EMPTY OU] Aucune sous-OU candidate pour la suppression (seule l'OU racine a été scannée ou aucune sous-OU n'existait).");
                return;
            }
            
            _logService.Log("EMPTY_OU_CLEANUP_INFO", $"[EMPTY OU] {candidateOusForDeletion.Count} sous-OUs candidates pour la vérification de vacuité. Ex: {string.Join("; ", candidateOusForDeletion.Take(3))}{(candidateOusForDeletion.Count > 3 ? "..." : "")}");

            int ousMarkedForDeletion = 0;
            foreach (var ouDn in candidateOusForDeletion)
            {
                try
                {
                    _logger.LogDebug($"[EMPTY OU DEBUG] Vérification de la vacuité de l'OU: {ouDn}");
                    bool isEmpty = await _ldapService.IsOrganizationalUnitEmptyAsync(ouDn);
                    if (isEmpty)
                    {
                        _logService.Log("EMPTY_OU_CLEANUP_INFO", $"[EMPTY OU] L'OU '{ouDn}' est vide et sera marquée pour suppression.");
                        analysis.Actions.Add(new ImportAction
                        {
                            ActionType = ActionType.DELETE_OU,
                            ObjectName = ExtractOuName(ouDn), // Utiliser le nom de l'OU plutôt que le DN complet pour ObjectName
                            Path = ouDn,
                            Message = $"Suppression de l'unité organisationnelle vide: '{ouDn}'",
                            Attributes = new Dictionary<string, string>()
                        });
                        ousMarkedForDeletion++;
                    }
                    else
                    {
                        _logger.LogDebug($"[EMPTY OU DEBUG] L'OU '{ouDn}' n'est pas vide.");
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError("EMPTY_OU_CLEANUP_ERROR", $"[EMPTY OU] Erreur lors de la vérification si l'OU '{ouDn}' est vide: {ex.Message}", ex);
                    analysis.Actions.Add(new ImportAction {
                        ActionType = ActionType.ERROR, ObjectName = $"Erreur vérification vacuité OU - {ExtractOuName(ouDn)}", Path = ouDn,
                        Message = $"Impossible de vérifier si l'OU '{ouDn}' est vide: {ex.Message}",
                        Attributes = new Dictionary<string, string>()
                    });
                }
            }
            _logService.Log("EMPTY_OU_CLEANUP_INFO", $"[EMPTY OU] Fin du processus de vérification des OUs vides. {ousMarkedForDeletion} OUs marquées pour suppression.");
        }
        #endregion
    }
}
