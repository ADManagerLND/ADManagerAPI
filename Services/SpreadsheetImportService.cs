using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Services.Parse;
using ADManagerAPI.Services.Utilities;

namespace ADManagerAPI.Services
{
    public class SpreadsheetImportService : ISpreadsheetImportService
    {
        private readonly ILdapService _ldapService;
        private readonly ILogService _logService;
        private readonly ILogger<SpreadsheetImportService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IFolderManagementService _folderManagementService;
        
        private readonly IEnumerable<ISpreadsheetParserService> _parsers;
        
        private readonly bool _enableOrphanCleanup = true; // Mettez à false pour désactiver globalement cette fonctionnalité

        public SpreadsheetImportService(
            IEnumerable<ISpreadsheetParserService> parsers,
            ILdapService ldapService, 
            ILogService logService, 
            ILogger<SpreadsheetImportService> logger,
            IServiceScopeFactory serviceScopeFactory,
            IFolderManagementService folderManagementService)
        {
            _parsers           = parsers ?? throw new ArgumentNullException(nameof(parsers));
            _ldapService = ldapService ?? throw new ArgumentNullException(nameof(ldapService));
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
            _folderManagementService = folderManagementService ?? throw new ArgumentNullException(nameof(folderManagementService));
        }

        #region Analyse du fichier (CSV/Excel)

        public async Task<AnalysisResult> AnalyzeSpreadsheetContentAsync(Stream fileStream, string fileName, ImportConfig config)
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

            _logger.LogInformation($"Nouvelle analyse de fichier ({fileName}) démarrée");

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

                FileDataStore.SetCsvData(spreadsheetData);
                return await AnalyzeSpreadsheetDataAsync(spreadsheetData, config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors de l'analyse du contenu du fichier: {ex.Message}");
                return new AnalysisResult
                {
                    Success = false,
                    ErrorMessage = $"Une erreur est survenue lors de l'analyse du fichier: {ex.Message}"
                };
            }
        }

        public async Task<AnalysisResult> AnalyzeSpreadsheetDataAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config)
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Analyse de données de tableur déjà chargées");

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

                var analysis = await AnalyzeSpreadsheetDataForActionsAsync(spreadsheetData, config);
                _logger.LogInformation($"[SpreadsheetImportService_DEBUG] After awaiting AnalyzeSpreadsheetDataForActionsAsync, 'analysis' object is null: {(analysis == null)}. Action count if not null: {analysis?.Actions?.Count ?? -1}"); // AJOUT DE CE LOG

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
                _logger.LogInformation($"Analyse de données de tableur terminée en {stopwatch.ElapsedMilliseconds} ms");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors de l'analyse des données de tableur: {ex.Message}");
                return new AnalysisResult
                {
                    Success = false,
                    ErrorMessage = $"Une erreur est survenue lors de l'analyse des données de tableur: {ex.Message}"
                };
            }
        }

        public async Task<ImportAnalysis> AnalyzeSpreadsheetDataForActionsAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config)
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
                if (ShouldProcessOrganizationalUnits(config))
                {
                    await ProcessOrganizationalUnitsAsync(spreadsheetData, config, analysis);
                }

                await ProcessUsersAsync(spreadsheetData, config, analysis);
                
                if (_enableOrphanCleanup)
                {
                    scannedOusForOrphanCleanup = await ProcessOrphanedUsersAsync(spreadsheetData, config, analysis);
                    await ProcessEmptyOrganizationalUnitsAsync(scannedOusForOrphanCleanup, config, analysis);
                }

                UpdateAnalysisSummary(analysis);
                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de l'analyse des données de tableur pour actions");
                throw;
            }
        }

        private bool ShouldProcessOrganizationalUnits(ImportConfig config)
        {
            return config.CreateMissingOUs && !string.IsNullOrEmpty(config.ouColumn);
        }

        private async Task ProcessOrganizationalUnitsAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, ImportAnalysis analysis)
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

        private List<string> ExtractUniqueOuValues(List<Dictionary<string, string>> spreadsheetData, ImportConfig config)
        {
            _logger.LogDebug("[OU_DEBUG] Début de ExtractUniqueOuValues.");
            var extractedValues = spreadsheetData
                .Where(row => row.ContainsKey(config.ouColumn) && !string.IsNullOrEmpty(row[config.ouColumn]))
                .Select(row => {
                    var rawValue = row[config.ouColumn];
                    var trimmedValue = rawValue.Trim();
                    _logger.LogTrace($"[OU_DEBUG] Valeur OU brute du fichier: '{rawValue}', après Trim: '{trimmedValue}'");
                    return trimmedValue;
                })
                .Where(trimmedValue => !string.IsNullOrEmpty(trimmedValue))
                .Distinct(StringComparer.OrdinalIgnoreCase) 
                .ToList();
            _logger.LogInformation($"[OU_DEBUG] {extractedValues.Count} valeurs d'OU uniques (insensible à la casse, après Trim) extraites du fichier: {JsonSerializer.Serialize(extractedValues)}");
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

        private async Task ProcessUsersAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, ImportAnalysis analysis)
        {
            var ousToBeCreated = new HashSet<string>(
                analysis.Actions.Where(a => a.ActionType == ActionType.CREATE_OU).Select(a => a.Path),
                StringComparer.OrdinalIgnoreCase
            );
            var knownExistingOuPathsCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in spreadsheetData)
                await ProcessUserRowAsync(row, config, analysis, ousToBeCreated, knownExistingOuPathsCache);
        }

        private async Task ProcessUserRowAsync(Dictionary<string, string> row, ImportConfig config, ImportAnalysis analysis, HashSet<string> ousToBeCreated, HashSet<string> knownExistingOuPathsCache)
        {
            var mappedRow = MapRow(row, config);
            string? samAccountName = mappedRow.GetValueOrDefault("sAMAccountName");

            // NOUVEAU LOG AJOUTÉ ICI
            _logger.LogInformation($"[SAM_DEBUG] Dans ProcessUserRowAsync, sAMAccountName obtenu depuis mappedRow: '{samAccountName}' (avant Split).");

            if (string.IsNullOrEmpty(samAccountName))
            {
                analysis.Actions.Add(new ImportAction
                {
                    ActionType = ActionType.ERROR,
                    ObjectName = "Unknown",
                    Path = config.DefaultOU,
                    Message = "samAccountName manquant dans les données mappées pour une ligne.",
                    Attributes = row
                });
                return;
            }
            
            string ouPath = DetermineUserOuPath(row, config);
            bool ouExists = false;
            bool existenceDetermined = false;

            if (string.IsNullOrEmpty(ouPath))
            {
                ouExists = true; 
                existenceDetermined = true;
            }
            else
            {
                if (ousToBeCreated.Contains(ouPath))
                {
                    ouExists = true;
                    existenceDetermined = true;
                    _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' planifiée pour création. Utilisateur: '{samAccountName}'.");
                }
                else if (knownExistingOuPathsCache.Contains(ouPath))
                {
                    ouExists = true;
                    existenceDetermined = true;
                    _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' trouvée dans cache existance. Utilisateur: '{samAccountName}'.");
                }
            }
            if (!existenceDetermined && !string.IsNullOrEmpty(ouPath))
            {
                _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' ni planifiée, ni en cache existance. Vérification AD. Utilisateur: '{samAccountName}'.");
                ouExists = await CheckOrganizationalUnitExistsAsync(ouPath);
                if (ouExists)
                {
                    knownExistingOuPathsCache.Add(ouPath); 
                    _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' existe dans l'AD (ajoutée au cache).");
                }
                else
                {
                    _logger.LogDebug($"[CACHE_OU_LOGIC] OU '{ouPath}' N'EXISTE PAS dans l'AD.");
                }
            }
            if (!ouExists && config.CreateMissingOUs && !string.IsNullOrEmpty(ouPath))
            {
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
                    ousToBeCreated.Add(ouPath); 
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
            
            string cleanedSamAccountName = samAccountName.Split('(')[0].Trim();
             // NOUVEAU LOG AJOUTÉ ICI
            _logger.LogInformation($"[SAM_DEBUG] Dans ProcessUserRowAsync, cleanedSamAccountName: '{cleanedSamAccountName}' (après Split et Trim).");

            // Nouvelle section pour dédupliquer cleanedSamAccountName
            if (!string.IsNullOrEmpty(cleanedSamAccountName) && cleanedSamAccountName.Length > 0 && cleanedSamAccountName.Length % 2 == 0)
            {
                int half = cleanedSamAccountName.Length / 2;
                string firstHalf = cleanedSamAccountName.Substring(0, half);
                string secondHalf = cleanedSamAccountName.Substring(half);
                if (firstHalf.Equals(secondHalf, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"[SPREADSHEET_HEURISTIC] cleanedSamAccountName '{cleanedSamAccountName}' dans SpreadsheetImportService semble dupliqué. Utilisation du premier moitié '{firstHalf}'.");
                    cleanedSamAccountName = firstHalf;
                    _logger.LogInformation($"[SAM_DEBUG] cleanedSamAccountName après heuristique dans SpreadsheetImportService: '{cleanedSamAccountName}'.");
                }
            }
            // Fin de la nouvelle section

            var userExists = await _ldapService.UserExistsAsync(cleanedSamAccountName);
            ActionType userActionType = userExists ? ActionType.UPDATE_USER : ActionType.CREATE_USER;
            string userActionMessage = userExists ? "Mise à jour d'un utilisateur existant" : "Création d'un nouvel utilisateur";

            // Ensure user attributes are a fresh dictionary for this action
            var userAttributes = new Dictionary<string, string>(mappedRow);

            analysis.Actions.Add(new ImportAction
            {
                ActionType = userActionType,
                ObjectName = cleanedSamAccountName,
                Path = ouPath,
                Message = userActionMessage,
                Attributes = userAttributes 
            });
            _logger.LogInformation($"Added {userActionType} action for user {cleanedSamAccountName} in OU {ouPath}");


            // --- BEGIN SECTION FOR PROVISIONING USER SHARE ---
            if (config.Folders != null && config.Folders.EnableShareProvisioning)
            {
                _logger.LogInformation($"Vérification de la configuration pour le provisionnement du partage utilisateur pour {cleanedSamAccountName}.");
                string? serverName = config.Folders.TargetServerName;
                // string? baseNtfsPath = config.Folders.ShareNameForUserFolders; // ANCIENNE LOGIQUE, incorrecte
                string? localPathOnServer = config.Folders.LocalPathForUserShareOnServer; // NOUVEAU: ex C:\Data\Eleves
                string? shareNameForUserDirs = config.Folders.ShareNameForUserFolders;    // NOUVEAU: ex "Eleves"
                string? netbiosDomainName = config.NetBiosDomainName; 
                List<string> subfoldersForShare = config.Folders.DefaultShareSubfolders ?? new List<string>();

                if (string.IsNullOrWhiteSpace(serverName))
                {
                    _logger.LogWarning($"TargetServerName n'est pas configuré dans config.Folders. Le provisionnement du partage utilisateur pour {cleanedSamAccountName} est ignoré.");
                }
                // else if (string.IsNullOrWhiteSpace(baseNtfsPath)) // ANCIENNE VERIFICATION
                // {
                //    _logger.LogWarning($"BaseNtfsPath n'est pas configuré dans config.Folders. Le provisionnement du partage utilisateur pour {cleanedSamAccountName} est ignoré.");
                // }
                else if (string.IsNullOrWhiteSpace(localPathOnServer))
                {
                    _logger.LogWarning($"LocalPathForUserShareOnServer n'est pas configuré dans config.Folders. Le provisionnement du partage utilisateur pour {cleanedSamAccountName} est ignoré.");
                }
                else if (string.IsNullOrWhiteSpace(shareNameForUserDirs))
                {
                    _logger.LogWarning($"ShareNameForUserFolders n'est pas configuré dans config.Folders. Le provisionnement du partage utilisateur pour {cleanedSamAccountName} est ignoré.");
                }
                else if (string.IsNullOrWhiteSpace(netbiosDomainName))
                {
                    _logger.LogWarning($"NetBiosDomainName n'est pas configuré dans config. Le provisionnement du partage utilisateur pour {cleanedSamAccountName} est ignoré.");
                }
                else
                {
                    string accountAd = $"{netbiosDomainName}\\{cleanedSamAccountName}";
                    // string userSharePath = System.IO.Path.Combine(baseNtfsPath, cleanedSamAccountName); // ANCIEN: le chemin physique est construit DANS FolderManagementService
                    string individualShareName = cleanedSamAccountName + "$"; // Le nom du partage individuel (ex: jdupont$)

                    var shareAttributes = new Dictionary<string, string>(mappedRow) 
                    {
                        ["ServerName"] = serverName,
                        // ["BaseNtfsPath"] = baseNtfsPath, // ANCIEN
                        ["LocalPathForUserShareOnServer"] = localPathOnServer,     // NOUVEAU
                        ["ShareNameForUserFolders"] = shareNameForUserDirs,        // NOUVEAU
                        ["AccountAd"] = accountAd,
                        ["Subfolders"] = System.Text.Json.JsonSerializer.Serialize(subfoldersForShare),
                        // ["UserSharePath"] = userSharePath, // Info, plus pertinent de le construire dans FMS
                        ["IndividualShareName"] = individualShareName // Nom du partage individuel SMB
                    };

                    analysis.Actions.Add(new ImportAction
                    {
                        ActionType = ActionType.PROVISION_USER_SHARE, 
                        ObjectName = individualShareName, // Utiliser le nom du partage individuel comme ObjectName
                        Path = localPathOnServer, // Chemin physique de base sur le serveur où les dossiers seront créés (ex: C:\Data\Eleves)
                        Message = $"Préparation du provisionnement du partage utilisateur '{individualShareName}' pour '{accountAd}' sur '{serverName}'. Partage principal: '{shareNameForUserDirs}'.",
                        Attributes = shareAttributes
                    });
                    _logger.LogInformation($"Ajout de l'action PROVISION_USER_SHARE pour '{accountAd}' (partage individuel : '{individualShareName}', basé sur '{shareNameForUserDirs}').");
                }
            }
            // --- END SECTION FOR PROVISIONING USER SHARE ---

            // Logic for creating class group folder actions
            if (config.ClassGroupFolderCreationConfig != null)
            {
                string shouldCreateClassGroupFolderVal = mappedRow.GetValueOrDefault(config.ClassGroupFolderCreationConfig.CreateClassGroupFolderColumnName ?? "CreateClassGroupFolder");
                if (bool.TryParse(shouldCreateClassGroupFolderVal, out bool createClassGroupFolder) && createClassGroupFolder)
                {
                    string? classGroupId = mappedRow.GetValueOrDefault(config.ClassGroupFolderCreationConfig.ClassGroupIdColumnName ?? "ClassGroupId");
                    string? classGroupName = mappedRow.GetValueOrDefault(config.ClassGroupFolderCreationConfig.ClassGroupNameColumnName ?? "ClassGroupName");
                    string classGroupTemplateName = mappedRow.GetValueOrDefault(config.ClassGroupFolderCreationConfig.ClassGroupTemplateNameColumnName ?? "ClassGroupTemplateName", "DefaultClassGroupTemplate"); // Default template name

                    if (!string.IsNullOrWhiteSpace(classGroupId) && !string.IsNullOrWhiteSpace(classGroupName))
                    {
                        var classGroupFolderAttributes = new Dictionary<string, string>(mappedRow)
                        {
                            ["Id"] = classGroupId,
                            ["Name"] = classGroupName,
                            ["TemplateName"] = classGroupTemplateName
                        };

                        analysis.Actions.Add(new ImportAction
                        {
                            ActionType = ActionType.CREATE_CLASS_GROUP_FOLDER,
                            ObjectName = classGroupName,
                            Path = "", // Path for class group folder is determined by FolderManagementService
                            Message = $"Préparation de la création du dossier pour le groupe de classes {classGroupName} (Modèle: {classGroupTemplateName})",
                            Attributes = classGroupFolderAttributes
                        });
                        _logger.LogInformation($"Added CREATE_CLASS_GROUP_FOLDER action for {classGroupName}");
                    }
                    else
                    {
                        _logger.LogWarning($"Skipping CREATE_CLASS_GROUP_FOLDER for {cleanedSamAccountName} due to missing/invalid data: Id='{classGroupId}', Name='{classGroupName}', Template='{classGroupTemplateName}'. " +
                                           $"Ensure columns '{(config.ClassGroupFolderCreationConfig.ClassGroupIdColumnName ?? "ClassGroupId")}', " +
                                           $"'{(config.ClassGroupFolderCreationConfig.ClassGroupNameColumnName ?? "ClassGroupName")}', " +
                                           $"'{(config.ClassGroupFolderCreationConfig.ClassGroupTemplateNameColumnName ?? "ClassGroupTemplateName")}' are correctly mapped and populated.");
                    }
                }
            }
            
            // Logic for creating Team group actions (placeholder)
            if (config.TeamGroupCreationConfig != null)
            {
                string shouldCreateTeamGroupVal = mappedRow.GetValueOrDefault(config.TeamGroupCreationConfig.CreateTeamGroupColumnName ?? "CreateTeamGroup");
                if (bool.TryParse(shouldCreateTeamGroupVal, out bool createTeamGroup) && createTeamGroup)
                {
                    string? teamGroupName = mappedRow.GetValueOrDefault(config.TeamGroupCreationConfig.TeamGroupNameColumnName ?? "TeamGroupName");
                    // Potentially more attributes like TeamOwners, TeamMembers, TemplateType etc.

                    if (!string.IsNullOrWhiteSpace(teamGroupName))
                    {
                        var teamGroupAttributes = new Dictionary<string, string>(mappedRow)
                        {
                            ["Name"] = teamGroupName
                            // Add other relevant attributes from mappedRow or defaults
                        };

                        analysis.Actions.Add(new ImportAction
                        {
                            ActionType = ActionType.CREATE_TEAM_GROUP,
                            ObjectName = teamGroupName,
                            Path = "", // Path/ID for Team groups might be handled differently or not applicable
                            Message = $"Préparation de la création du groupe Teams: {teamGroupName}",
                            Attributes = teamGroupAttributes 
                        });
                        _logger.LogInformation($"Added CREATE_TEAM_GROUP action for {teamGroupName}. (Note: Execution logic is a placeholder).");
                    }
                    else
                    {
                         _logger.LogWarning($"Skipping CREATE_TEAM_GROUP for {cleanedSamAccountName} due to missing/invalid TeamGroupName. "+
                                           $"Ensure column '{(config.TeamGroupCreationConfig.TeamGroupNameColumnName ?? "TeamGroupName")}' is correctly mapped and populated.");
                    }
                }
            }
            // --- END SECTION FOR ADDING FOLDER AND TEAM ACTIONS ---

            // Si aucune action n'a été générée pour cet utilisateur (création/màj/déplacement), on logue un skip
            bool actionGeneratedForUser = false;
            if (userActionType == ActionType.CREATE_USER || userActionType == ActionType.UPDATE_USER || userActionType == ActionType.MOVE_USER)
            {
                actionGeneratedForUser = true;
            }

            if (!actionGeneratedForUser)
            {
                _logger.LogWarning($"Aucune action générée pour l'utilisateur {cleanedSamAccountName}. Utilisation de l'OU par défaut pour l'utilisateur.");
                ouPath = config.DefaultOU;
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
            analysis.Summary.CreateOUCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_OU);
            analysis.Summary.DeleteOUCount = analysis.Actions.Count(a => a.ActionType == ActionType.DELETE_OU);
            analysis.Summary.MoveCount = analysis.Actions.Count(a => a.ActionType == ActionType.MOVE_USER);
            analysis.Summary.CreateStudentFolderCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_STUDENT_FOLDER);
            analysis.Summary.CreateClassGroupFolderCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_CLASS_GROUP_FOLDER);
            analysis.Summary.CreateTeamGroupCount = analysis.Actions.Count(a => a.ActionType == ActionType.CREATE_TEAM_GROUP);
            analysis.Summary.ProvisionUserShareCount = analysis.Actions.Count(a => a.ActionType == ActionType.PROVISION_USER_SHARE); // Ajout
        }

        #endregion

        #region Import du fichier

        public async Task<ImportResult> ExecuteImportFromAnalysisAsync(ImportAnalysis analysis, ImportConfig config, string? connectionId = null)
        {
            var sw = Stopwatch.StartNew();
            _logger.LogInformation("Début de l'import depuis une analyse existante.");

            try
            {
                // config = ImportConfigHelpers.EnsureValidConfig(config, _logger); // Config should already be valid if analysis was done with it.

                if (analysis == null || analysis.Actions == null || !analysis.Actions.Any())
                {
                    _logger.LogWarning("Aucune action fournie dans l'objet ImportAnalysis pour l'import.");
                    return new ImportResult
                    {
                        Success = false,
                        Summary = analysis?.Summary ?? new ImportSummary { TotalObjects = 0 },
                        ActionResults = new List<ImportActionResult>
                        { 
                            new ImportActionResult 
                            { 
                                Success = false, 
                                Message = "Aucune action fournie dans l'objet ImportAnalysis."
                            }
                        },
                        Details = "Aucune action fournie dans l'objet ImportAnalysis."
                    };
                }

                // La configuration d'import est déjà appliquée lors de la phase d'analyse qui a produit l'objet 'analysis'.
                // On peut donc directement utiliser analysis.Actions.
                return await ExecuteImport(analysis.Actions, config, connectionId); 
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de l'import depuis une analyse existante.");
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
                    Summary = analysis?.Summary ?? new ImportSummary { ErrorCount = 1 }
                };
            }
            finally
            {
                sw.Stop();
                _logger.LogInformation($"Import depuis analyse terminé en {sw.ElapsedMilliseconds} ms");
            }
        }
        
        public async Task<ImportResult> ExecuteImportFromActionsAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, List<LegacyImportActionItem> actions, string? connectionId = null)
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
                    ActionType.CREATE_STUDENT_FOLDER,
                    ActionType.CREATE_CLASS_GROUP_FOLDER,
                    ActionType.CREATE_TEAM_GROUP,
                    ActionType.PROVISION_USER_SHARE, // Ajout pour traitement séquentiel
                    ActionType.MOVE_USER,
                    ActionType.DELETE_USER,
                    ActionType.DELETE_OU
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
                    if (actionTypeToProcess == ActionType.CREATE_OU || actionTypeToProcess == ActionType.DELETE_OU ||
                        actionTypeToProcess == ActionType.CREATE_STUDENT_FOLDER || actionTypeToProcess == ActionType.CREATE_CLASS_GROUP_FOLDER ||
                        actionTypeToProcess == ActionType.CREATE_TEAM_GROUP)
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
                result.Summary.CreateStudentFolderCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_STUDENT_FOLDER && ar.Success);
                result.Summary.CreateClassGroupFolderCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_CLASS_GROUP_FOLDER && ar.Success);
                result.Summary.CreateTeamGroupCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_TEAM_GROUP && ar.Success);
                result.Summary.ProvisionUserShareCount = result.ActionResults.Count(ar => ar.ActionType == ActionType.PROVISION_USER_SHARE && ar.Success); // Ajout
                
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
                        $"Import terminé avec {result.Summary.CreateCount} créations, {result.Summary.UpdateCount} mises à jour, {result.Summary.DeleteCount} suppressions, {result.Summary.CreateStudentFolderCount} dossiers étudiants créés, {result.Summary.CreateClassGroupFolderCount} dossiers groupes créés, {result.Summary.CreateTeamGroupCount} groupes Teams créés, {result.Summary.ProvisionUserShareCount} partages utilisateurs provisionnés et {result.Summary.ErrorCount} erreurs", // Ajout au message
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
                    case ActionType.CREATE_STUDENT_FOLDER:
                        break;
                    case ActionType.CREATE_CLASS_GROUP_FOLDER:
                        break;
                    case ActionType.CREATE_TEAM_GROUP:
                        break;
                    case ActionType.PROVISION_USER_SHARE: // Ajout
                        break;
                }
            }

            if (signalRService != null && !string.IsNullOrEmpty(connectionId))
            {
                int progressPercentage = (int)((processedCount * 100.0) / totalCount);
                string message = $"Traitement: {action.ActionType} pour {action.ObjectName} - {processedCount}/{totalCount}";

                var summaryForProgress = new ImportSummary
                {
                    TotalObjects = totalCount,
                    CreateCount = currentImportResult.CreatedCount,
                    UpdateCount = currentImportResult.UpdatedCount,
                    DeleteCount = currentImportResult.DeletedCount,
                    MoveCount = currentImportResult.MovedCount,
                    ErrorCount = currentImportResult.ActionResults.Count(ar => !ar.Success),
                    CreateOUCount = currentImportResult.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_OU && ar.Success),
                    DeleteOUCount = currentImportResult.ActionResults.Count(ar => ar.ActionType == ActionType.DELETE_OU && ar.Success),
                    CreateStudentFolderCount = currentImportResult.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_STUDENT_FOLDER && ar.Success),
                    CreateClassGroupFolderCount = currentImportResult.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_CLASS_GROUP_FOLDER && ar.Success),
                    CreateTeamGroupCount = currentImportResult.ActionResults.Count(ar => ar.ActionType == ActionType.CREATE_TEAM_GROUP && ar.Success),
                    ProvisionUserShareCount = currentImportResult.ActionResults.Count(ar => ar.ActionType == ActionType.PROVISION_USER_SHARE && ar.Success), // Ajout
                    ProcessedCount = processedCount
                };
                
                var currentAnalysisForProgress = new ImportAnalysis
                {
                    Summary = summaryForProgress
                };

                signalRService.SendCsvAnalysisProgressAsync(
                    connectionId,
                    progressPercentage,
                    currentPhase,
                    message,
                    currentAnalysisForProgress
                ).GetAwaiter().GetResult();
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
                        // await Task.Run(() => _ldapService.DeleteOrganizationalUnitAsync(action.Path)); // Ancien appel
                        bool ouDeleted = await _ldapService.DeleteOrganizationalUnitAsync(action.Path, false); // `deleteIfNotEmpty` mis à false par défaut
                        if (ouDeleted)
                        {
                            actionResult.Success = true;
                            actionResult.Message = $"Suppression de l'unité organisationnelle {action.ObjectName} ({action.Path}) effectuée.";
                        }
                        else
                        {
                            actionResult.Success = false;
                            actionResult.Message = $"Échec de la suppression de l'OU {action.ObjectName} ({action.Path}). Elle n'est probablement pas vide ou une autre erreur s'est produite (voir logs LdapService).";
                            _logger.LogWarning($"Tentative de suppression de l'OU {action.Path} pour l'objet {action.ObjectName} a échoué (probablement non vide ou autre erreur LdapService).");
                        }
                        break;
                    case ActionType.CREATE_STUDENT_FOLDER:
                        _logger.LogInformation($"Attempting to create student folder for: {action.ObjectName}");
                        if (action.Attributes.TryGetValue("UserRole", out var roleStr) && Enum.TryParse<UserRole>(roleStr, true, out var userRole))
                        {
                            var studentInfo = new StudentInfo { 
                                Id = action.Attributes.GetValueOrDefault("Id", action.ObjectName), 
                                Name = action.Attributes.GetValueOrDefault("Name", action.ObjectName) 
                            };
                            // TODO: Determine the correct templateName. Using a placeholder for now.
                            string studentTemplateName = action.Attributes.GetValueOrDefault("TemplateName", "DefaultStudentTemplate"); 
                            await _folderManagementService.CreateStudentFolderAsync(studentInfo, studentTemplateName, userRole);
                            actionResult.Success = true;
                            actionResult.Message = $"Création du dossier étudiant pour {action.ObjectName} effectuée (modèle: {studentTemplateName}).";
                        }
                        else
                        {
                            actionResult.Success = false;
                            actionResult.Message = $"UserRole manquant ou invalide pour la création du dossier étudiant {action.ObjectName}. Attributs: {JsonSerializer.Serialize(action.Attributes)}";
                            _logger.LogWarning(actionResult.Message);
                        }
                        break;
                    case ActionType.CREATE_CLASS_GROUP_FOLDER:
                        _logger.LogInformation($"Attempting to create class group folder for: {action.ObjectName}");
                        var classGroupInfo = new ClassGroupInfo { 
                            Id = action.Attributes.GetValueOrDefault("Id", action.ObjectName), 
                            Name = action.Attributes.GetValueOrDefault("Name", action.ObjectName) 
                        };
                        // TODO: Determine the correct templateName. Using a placeholder for now.
                        string classGroupTemplateName = action.Attributes.GetValueOrDefault("TemplateName", "DefaultClassGroupTemplate");
                        await _folderManagementService.CreateClassGroupFolderAsync(classGroupInfo, classGroupTemplateName);
                        actionResult.Success = true;
                        actionResult.Message = $"Création du dossier de groupe de classes pour {action.ObjectName} effectuée (modèle: {classGroupTemplateName}).";
                        break;
                    case ActionType.CREATE_TEAM_GROUP:
                        _logger.LogWarning($"L'action CREATE_TEAM_GROUP n'est pas encore implémentée. Tentative pour: {action.ObjectName}");
                        actionResult.Success = false;
                        actionResult.Message = $"Création de groupe Team pour {action.ObjectName} non implémentée.";
                        break;
                    case ActionType.PROVISION_USER_SHARE:
                        _logger.LogInformation($"Tentative de provisionnement du partage utilisateur pour: {action.ObjectName} avec les attributs: {System.Text.Json.JsonSerializer.Serialize(action.Attributes)}");
                        string serverName = action.Attributes.GetValueOrDefault("ServerName");
                        // string baseNtfsPath = action.Attributes.GetValueOrDefault("BaseNtfsPath"); // ANCIEN
                        string localPathOnServer = action.Attributes.GetValueOrDefault("LocalPathForUserShareOnServer"); // NOUVEAU
                        string configuredShareName = action.Attributes.GetValueOrDefault("ShareNameForUserFolders");   // NOUVEAU
                        string accountAd = action.Attributes.GetValueOrDefault("AccountAd");
                        string subfoldersJson = action.Attributes.GetValueOrDefault("Subfolders");
                        List<string> subfolders = new List<string>();
                        if (!string.IsNullOrWhiteSpace(subfoldersJson)) 
                        {
                            try { subfolders = System.Text.Json.JsonSerializer.Deserialize<List<string>>(subfoldersJson) ?? new List<string>(); }
                            catch (Exception ex) { _logger.LogError(ex, $"Erreur lors de la désérialisation de Subfolders pour PROVISION_USER_SHARE pour {action.ObjectName}"); }
                        }

                        // if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(baseNtfsPath) || string.IsNullOrWhiteSpace(accountAd)) // ANCIENNE CONDITION
                        if (string.IsNullOrWhiteSpace(serverName) || 
                            string.IsNullOrWhiteSpace(localPathOnServer) || 
                            string.IsNullOrWhiteSpace(configuredShareName) || 
                            string.IsNullOrWhiteSpace(accountAd))
                        {
                            actionResult.Success = false;
                            actionResult.Message = $"Attributs manquants (ServerName, LocalPathForUserShareOnServer, ShareNameForUserFolders, ou AccountAd) pour le provisionnement du partage utilisateur {action.ObjectName}.";
                            // actionResult.Message = $"Attributs manquants (ServerName, BaseNtfsPath, ou AccountAd) pour le provisionnement du partage utilisateur {action.ObjectName}."; // Ligne dupliquée supprimée
                            _logger.LogWarning(actionResult.Message + $" Attributs reçus: {System.Text.Json.JsonSerializer.Serialize(action.Attributes)}");
                        }
                        else
                        {
                            // bool provisionSuccess = await _folderManagementService.ProvisionUserShareAsync(serverName, baseNtfsPath, accountAd, subfolders); // ANCIEN APPEL
                            bool provisionSuccess = await _folderManagementService.ProvisionUserShareAsync(serverName, localPathOnServer, configuredShareName, accountAd, subfolders); // NOUVEL APPEL
                            actionResult.Success = provisionSuccess;
                            actionResult.Message = $"{(provisionSuccess ? "Provisionnement" : "Échec du provisionnement")} du partage utilisateur '{action.ObjectName}' pour '{accountAd}' sur '{serverName}'.";
                            if (!provisionSuccess)
                            {
                                _logger.LogError($"Échec de ProvisionUserShareAsync pour {action.ObjectName}. Message du service FolderManagement attendu dans ses propres logs.");
                            }
                        }
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

        public async Task<ImportResult> ProcessSpreadsheetDataAsync(List<Dictionary<string, string>> data, ImportConfig config)
        {
            _logger.LogInformation($"Traitement des données de tableur: {data.Count} lignes");
            
            var analysisResult = await AnalyzeSpreadsheetDataAsync(data, config);
            
            var actions = new List<LegacyImportActionItem>();
            
            if (analysisResult.IsValid)
            {
                // Logic to determine actions based on analysis
            }
            
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

                // NOUVEAU LOG AJOUTÉ ICI
                if (string.Equals(adAttribute, "sAMAccountName", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"[SAM_DEBUG] Attribut sAMAccountName: template '{template}' a produit la valeur brute: '{finalValue}' pour la ligne CSV: {System.Text.Json.JsonSerializer.Serialize(row)}");
                }

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

            // NOUVEAU LOG AJOUTÉ ICI AVANT DE RETOURNER
            _logger.LogInformation($"[SAM_DEBUG] Valeur finale de sAMAccountName mappée avant retour de MapRow: '{result.GetValueOrDefault("sAMAccountName")}' pour la ligne CSV: {System.Text.Json.JsonSerializer.Serialize(row)}");
            
            _logger.LogDebug($"Fin du mappage de la ligne CSV. Attributs AD mappés: {System.Text.Json.JsonSerializer.Serialize(result)}");
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

            // Normalisation pour décomposer les caractères accentués
            string normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (char c in normalizedString)
            {
                // Conserver les lettres, les chiffres et le point
                if (char.IsLetterOrDigit(c) || c == '.')
                {
                    stringBuilder.Append(c);
                }
                // Remplacer certains caractères spécifiques s'ils ne sont pas déjà décomposés et filtrés
                // Ceci est un exemple simple, une cartographie plus complète peut être nécessaire
                else if (c == 'é' || c == 'è' || c == 'ê' || c == 'ë') stringBuilder.Append('e');
                else if (c == 'à' || c == 'â') stringBuilder.Append('a');
                else if (c == 'ç') stringBuilder.Append('c');
                else if (c == 'ù' || c == 'û') stringBuilder.Append('u');
                else if (c == 'î' || c == 'ï') stringBuilder.Append('i');
                else if (c == 'ô') stringBuilder.Append('o');
                // Ignorer les marques non espaçantes qui n'ont pas été transformées en lettres simples
                else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    // Conserver les caractères qui ne sont pas des marques non espaçantes et qui ne sont pas explicitement remplacés
                    // mais qui pourraient être problématiques pour un sAMAccountName. Un filtrage plus strict est souvent appliqué ici.
                    // Pour l'instant, on les laisse passer s'ils ne sont pas des marques non espaçantes.
                    // stringBuilder.Append(c); // Optionnel: si on veut garder plus de caractères
                }
            }
            // Re-normaliser peut être utile, mais ici on a construit une chaîne ASCII-like
            string result = stringBuilder.ToString();
            
            // Si après le traitement, la chaîne est vide (ex: entrée "?" ), retourner une valeur par défaut ou l'original pour éviter les erreurs.
            return string.IsNullOrWhiteSpace(result) ? text : result;
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

        #region Orphan User Cleanup
        private async Task<List<string>> ProcessOrphanedUsersAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, ImportAnalysis analysis)
        {
            string rootOuForCleanup = config.DefaultOU;
            List<string> finalOusToScan = new List<string>();

            if (string.IsNullOrEmpty(rootOuForCleanup))
            {
              //  _logService.Log("IMPORT_ORPHANS_WARN", "[IMPORT ORPHANS] Nettoyage des orphelins activé, mais config.DefaultOU est vide. Annulé.");
                analysis.Actions.Add(new ImportAction { ActionType = ActionType.ERROR, ObjectName = "Config Nettoyage Orphelins", Message = "config.DefaultOU non configurée." });
                return finalOusToScan;
            }

           // _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Début nettoyage. OU racine: {rootOuForCleanup}");

            var allSamAccountNamesFromSpreadsheet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
           // _logService.Log("IMPORT_ORPHANS_INFO", "[IMPORT ORPHANS] Collecte sAMAccountNames depuis fichier.");
            foreach (var rowInSpreadsheet in spreadsheetData)
            {
                var mappedAttributes = MapRow(rowInSpreadsheet, config);
                if (!mappedAttributes.TryGetValue("sAMAccountName", out var samAccountNameFromMapping) || string.IsNullOrEmpty(samAccountNameFromMapping))
                {
                  //  _logger.LogWarning($"[ORPHAN_DEBUG] sAMAccountName non trouvé/vide. Attributs: {JsonSerializer.Serialize(mappedAttributes)}");
                    continue;
                }
                string cleanedSamForComparison = samAccountNameFromMapping.Split('(')[0].Trim();
                if (string.IsNullOrEmpty(cleanedSamForComparison))
                {
                  //   _logger.LogWarning($"[ORPHAN_DEBUG] sAMAccountName nettoyé vide. Original: '{samAccountNameFromMapping}'. Ignoré.");
                    continue;
                }
                allSamAccountNamesFromSpreadsheet.Add(cleanedSamForComparison);
            }
         //   _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] {allSamAccountNamesFromSpreadsheet.Count} sAMAccountNames collectés. Ex: {string.Join(", ", allSamAccountNamesFromSpreadsheet.Take(5))}{(allSamAccountNamesFromSpreadsheet.Count > 5 ? "..." : "")}");

            try
            {
                //_logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Récupération OUs à scanner récursivement depuis: {rootOuForCleanup}");
                finalOusToScan = await _ldapService.GetOrganizationalUnitPathsRecursiveAsync(rootOuForCleanup);
                if (!finalOusToScan.Any())
                {
                    // _logService.Log("IMPORT_ORPHANS_WARN", $"[IMPORT ORPHANS] Aucune OU trouvée pour scan. Nettoyage arrêté.");
                     return finalOusToScan;
                }
                // _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] {finalOusToScan.Count} OUs seront scannées. Ex: {string.Join("; ", finalOusToScan.Take(3))}{(finalOusToScan.Count > 3 ? "..." : "")}");
            }
            catch (Exception ex)
            {
              //  _logService.LogError("IMPORT_ORPHANS_ERROR", $"[IMPORT ORPHANS] Erreur listage OUs: {ex.Message}", ex);
                analysis.Actions.Add(new ImportAction { ActionType = ActionType.ERROR, ObjectName = $"Erreur Listage OUs - {rootOuForCleanup}", Message = $"Erreur: {ex.Message}" });
                return finalOusToScan;
            }
            
            int totalOrphanedUsersFound = 0;

            foreach (var currentOuToScan in finalOusToScan)
            {
               // _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Scan OU: '{currentOuToScan}'.");
                List<string> existingSamAccountNamesInCurrentOU;
                try
                {
                    existingSamAccountNamesInCurrentOU = await _ldapService.GetUsersInOUAsync(currentOuToScan);
                //    _logger.LogDebug($"[ORPHAN_DEBUG] Dans '{currentOuToScan}', utilisateurs AD: {existingSamAccountNamesInCurrentOU.Count}. Ex: {string.Join(", ", existingSamAccountNamesInCurrentOU.Take(5))}{(existingSamAccountNamesInCurrentOU.Count > 5 ? "..." : "")}");
                }
                catch (Exception ex)
                {
                    _logService.LogError("IMPORT_ORPHANS_ERROR", $"[IMPORT ORPHANS] Erreur listage utilisateurs OU '{currentOuToScan}': {ex.Message}", ex);
                    analysis.Actions.Add(new ImportAction { ActionType = ActionType.ERROR, ObjectName = $"Erreur Listage Utilisateurs AD - {currentOuToScan}", Message = $"Erreur: {ex.Message}"});
                    continue; 
                }

                if (!existingSamAccountNamesInCurrentOU.Any())
                {
                    _logger.LogDebug($"[ORPHAN_DEBUG] Aucun utilisateur AD dans '{currentOuToScan}'.");
                    continue;
                }

                int orphanedInThisOU = 0;
                foreach (var existingSamInAD in existingSamAccountNamesInCurrentOU)
                {
                    if (!allSamAccountNamesFromSpreadsheet.Contains(existingSamInAD)) 
                    {
                      //  _logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Orphelin: '{existingSamInAD}' dans '{currentOuToScan}'. Suppression ajoutée.");
                        analysis.Actions.Add(new ImportAction
                        {
                            ActionType = ActionType.DELETE_USER,
                            ObjectName = existingSamInAD,
                            Path = currentOuToScan, 
                            Message = $"Suppression orphelin '{existingSamInAD}' de '{currentOuToScan}' car non présent dans fichier.",
                            Attributes = new Dictionary<string, string>()
                        });
                        orphanedInThisOU++;
                        totalOrphanedUsersFound++;
                    }
                }
                //_logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Dans '{currentOuToScan}', {orphanedInThisOU} orphelins identifiés.");
            }
            //_logService.Log("IMPORT_ORPHANS_INFO", $"[IMPORT ORPHANS] Nettoyage terminé. {totalOrphanedUsersFound} orphelins identifiés sous '{rootOuForCleanup}'.");
            return finalOusToScan;
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
                    analysis.Actions.Add(new ImportAction { ActionType = ActionType.ERROR, ObjectName = $"Erreur vérification vacuité OU - {ExtractOuName(ouDn)}", Message = $"Erreur: {ex.Message}" });
                }
            }
            _logService.Log("EMPTY_OU_CLEANUP_INFO", $"[EMPTY OU] Fin du processus de vérification des OUs vides. {ousMarkedForDeletion} OUs marquées pour suppression.");
        }
        #endregion
    }
}
