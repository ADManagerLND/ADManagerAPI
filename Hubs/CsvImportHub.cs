using System.Collections.Concurrent;
using System.Text.Json;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Utils;
using Microsoft.AspNetCore.SignalR;

namespace ADManagerAPI.Hubs;

public class CsvImportHub : Hub
{
    private static readonly ConcurrentDictionary<string, List<LegacyImportActionItem>> _analysisActions = new();
    private static readonly ConcurrentDictionary<string, List<Dictionary<string, string>>> _fileData = new();
    private readonly IConfigService _configService;
    private readonly ILogger<CsvImportHub> _logger;
    private readonly ISignalRService _signalRService;
    private readonly ISpreadsheetImportService _spreadsheetImportService;

    public CsvImportHub(
        ILogger<CsvImportHub> logger,
        ISpreadsheetImportService spreadsheetImportService,
        IConfigService configService,
        ISignalRService signalRService)
    {
        _logger = logger;
        _spreadsheetImportService = spreadsheetImportService;
        _configService = configService;
        _signalRService = signalRService;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation($"Client connecté au CsvImportHub: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"Client déconnecté du CsvImportHub: {Context.ConnectionId}");

        _analysisActions.TryRemove(Context.ConnectionId, out _);
        _fileData.TryRemove(Context.ConnectionId, out _);
        FileDataStore.ClearAllData(Context.ConnectionId);

        AnalysisDataStore.ClearAnalysis(Context.ConnectionId);
        _logger.LogInformation($"[OnDisconnectedAsync] Données nettoyées pour ConnectionId: {Context.ConnectionId}");

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Parser un fichier brut avec la configuration appropriée (incluant manualColumns)
    /// </summary>
    private async Task<List<Dictionary<string, string>>?> ParseFileWithConfig(byte[] fileBytes, string fileName, ImportConfig config)
    {
        try
        {
            _logger.LogInformation($"🔧 Parsing de {fileName} avec config - ManualColumns: [{string.Join(", ", config.ManualColumns ?? new List<string>())}]");
            
            using var stream = new MemoryStream(fileBytes);
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            
            // Obtenir le bon parser
            var allParsers = Context.GetHttpContext()!.RequestServices.GetServices<ADManagerAPI.Services.Parse.ISpreadsheetDataParser>();
            var parser = allParsers.FirstOrDefault(p => p.CanHandle(extension));
            
            if (parser == null)
            {
                _logger.LogError($"Aucun parser trouvé pour {extension}");
                return null;
            }

            // 🔧 CORRECTION: Parser avec les bonnes manualColumns !
            var data = await parser.ParseAsync(stream, fileName, config.CsvDelimiter, config.ManualColumns);
            var columnNames = data.FirstOrDefault()?.Keys.ToList() ?? new List<string>();
            _logger.LogInformation($"✅ Parsing réussi: {data.Count} lignes, colonnes: [{string.Join(", ", columnNames)}]");
            
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erreur lors du parsing de {fileName}");
            return null;
        }
    }


    public async Task StartAnalysis(string configId, ImportConfig? customConfig = null, List<string>? disabledActionTypes = null)
    {
        _logger.LogInformation(
            $"🚀🚀🚀 StartAnalysis appelé par le client {Context.ConnectionId} avec configId: {configId}");
        
        _logger.LogInformation(
            $"🔧 STEP 0: METHODE APPELEE ! paramètres reçus - configId: '{configId}', customConfig: {(customConfig != null ? "OUI" : "NON")}, disabledActionTypes: {(disabledActionTypes?.Any() == true ? $"[{string.Join(", ", disabledActionTypes)}]" : "null/vide")}");
        
        if (customConfig != null)
        {
            _logger.LogInformation(
                $"🔧 CustomConfig.DisabledActionTypes: {(customConfig.DisabledActionTypes?.Any() == true ? string.Join(", ", customConfig.DisabledActionTypes) : "aucun")}");
            try 
            {
                var customConfigJson = System.Text.Json.JsonSerializer.Serialize(customConfig, new JsonSerializerOptions { WriteIndented = false });
                _logger.LogInformation($"🔧 CustomConfig JSON: {customConfigJson}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"🔧 Erreur sérialisation CustomConfig: {ex.Message}");
            }
        }

        // 🔍 LOGS DÉTAILLÉS pour debug
        _logger.LogInformation($"🔍 STEP 1: Vérification annulation connexion - IsCancellationRequested: {Context.ConnectionAborted.IsCancellationRequested}");
        
        try
        {
            _logger.LogInformation($"🔍 STEP 2: Envoi progression 10%");

        // S'assurer que la connexion est toujours active
        if (Context.ConnectionAborted.IsCancellationRequested)
        {
            _logger.LogWarning($"StartAnalysis: Connexion {Context.ConnectionId} a été annulée");
            return;
        }

            await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 10, "analyzing",
                "Analyse des données du fichier...");
            _logger.LogInformation($"🔍 STEP 3: Progression 10% envoyée");

            // 🔧 CORRECTION: Déterminer d'abord la configuration
            _logger.LogInformation($"🔍 STEP 4: Détermination de la configuration");
            ImportConfig config;
            
            // ✅ CORRECTION : Priorité à la configuration personnalisée (avec disabledActionTypes)
            if (customConfig != null)
            {
                _logger.LogInformation($"🔧 Utilisation de la configuration personnalisée avec {customConfig.DisabledActionTypes?.Count ?? 0} types d'actions désactivées");
                _logger.LogInformation($"🔍 STEP 5A: Configuration personnalisée sélectionnée");
                config = customConfig;
            }
            else if (string.IsNullOrEmpty(configId))
            {
                _logger.LogWarning(
                    "Aucun ID de configuration fourni pour l'analyse, utilisation d'une configuration par défaut");
                _logger.LogInformation($"🔍 STEP 5B: Configuration par défaut utilisée");
                config = new ImportConfig
                {
                    DefaultOU = "DC=domain,DC=local",
                    CsvDelimiter = ';'
                };
            }
            else
            {
                _logger.LogInformation($"🔍 STEP 5C: Chargement de la configuration {configId}");
                var savedConfigs = await _configService.GetSavedImportConfigs();
                _logger.LogInformation($"🔍 STEP 5C.1: {savedConfigs.Count} configurations chargées");
                var importConfig = savedConfigs.FirstOrDefault(c => c.Id == configId);
                if (importConfig == null)
                {
                    _logger.LogWarning($"Configuration {configId} non trouvée pour l'analyse");
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                        $"Configuration {configId} non trouvée");
                    return;
                }

                _logger.LogInformation($"🔍 STEP 5C.2: Configuration {configId} trouvée");
                config = importConfig.ConfigData;
                
                // ✅ FALLBACK : Si la config de base n'a pas de disabledActionTypes mais qu'on en a reçu en paramètre
                if ((config.DisabledActionTypes == null || !config.DisabledActionTypes.Any()) 
                    && disabledActionTypes != null && disabledActionTypes.Any())
                {
                    _logger.LogInformation($"🔧 Application des DisabledActionTypes du paramètre à la config de base: {string.Join(", ", disabledActionTypes)}");
                    config.DisabledActionTypes = disabledActionTypes
                        .Select(s => Enum.TryParse<ActionType>(s, true, out var actionType) ? (ActionType?)actionType : null)
                        .Where(a => a.HasValue)
                        .Select(a => a.Value)
                        .ToList();
                }
            }

            // 🔧 CORRECTION: Maintenant qu'on a la config, vérifier et parser les données si nécessaire
            _logger.LogInformation($"🔍 STEP 6: Vérification des données parsées");
            var spreadsheetData = FileDataStore.GetCsvData(Context.ConnectionId);
            _logger.LogInformation($"🔍 STEP 6.1: Données CSV existantes: {(spreadsheetData?.Count ?? 0)} lignes");
            
            // Si pas de données parsées, parser le fichier brut avec la bonne configuration
            if (spreadsheetData == null || spreadsheetData.Count == 0)
            {
                var rawFileData = FileDataStore.GetRawFileData(Context.ConnectionId);
                if (rawFileData == null)
                {
                    _logger.LogWarning("Aucune donnée de fichier trouvée (ni parsée ni brute)");
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                        "Aucune donnée de fichier trouvée. Veuillez d'abord uploader un fichier.");
                    return;
                }

                // Parser le fichier brut avec la configuration appropriée
                _logger.LogInformation($"🔧 Parsing du fichier brut avec la configuration pour connexion {Context.ConnectionId}");
                
                var fileName = rawFileData["fileName"] as string;
                var fileBytes = rawFileData["fileBytes"] as byte[];
                var extension = rawFileData["extension"] as string;
                
                if (fileBytes == null || string.IsNullOrEmpty(fileName))
                {
                    _logger.LogError("Données de fichier brut invalides");
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                        "Données de fichier invalides.");
                    return;
                }

                // Parser le fichier avec la bonne configuration
                spreadsheetData = await ParseFileWithConfig(fileBytes, fileName, config);
                
                if (spreadsheetData == null || spreadsheetData.Count == 0)
                {
                    _logger.LogError("Erreur lors du parsing avec configuration");
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                        "Impossible de parser le fichier avec la configuration fournie.");
                    return;
                }

                // Stocker les données parsées pour usage ultérieur
                FileDataStore.SetCsvData(spreadsheetData, Context.ConnectionId);
            }

            _logger.LogInformation(
                $"Données de fichier trouvées et associées à la connexion {Context.ConnectionId}: {spreadsheetData.Count} lignes");

            _logger.LogInformation($"🔍 STEP 7: Envoi progression 30% et démarrage analyse");
            await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 30, "analyzing",
                "Analyse des colonnes et validation des données...");

            _logger.LogInformation($"🔍 STEP 8: Appel AnalyzeSpreadsheetDataAsync avec {spreadsheetData.Count} lignes");
            var result = await _spreadsheetImportService.AnalyzeSpreadsheetDataAsync(spreadsheetData, config,
                Context.ConnectionId, Context.ConnectionAborted);
            _logger.LogInformation($"🔍 STEP 9: AnalyzeSpreadsheetDataAsync terminé - Success: {result.Success}");

            _logger.LogInformation(
                $"[StartAnalysis_HUB_LOG] Post-AnalyzeSpreadsheetDataAsync. ConnectionId: {Context.ConnectionId}. AnalysisResult.Success: {result.Success}. result.Analysis is null: {result.Analysis == null}. Actions if not null: {result.Analysis?.Actions?.Count ?? -1}. ErrorMessage: {result.ErrorMessage}");

            if (result.Success)
            {
                _logger.LogInformation($"[StartAnalysis_HUB_LOG] === ANALYSE RÉUSSIE POUR {Context.ConnectionId} ===");

                _logger.LogInformation(
                    $"[StartAnalysis_HUB_LOG] Analysis was successful. ConnectionId: {Context.ConnectionId}. Storing result.Analysis into AnalysisDataStore.");

                // Stocker l'analyse avec le connectionId
                AnalysisDataStore.SetAnalysis(Context.ConnectionId, result.Analysis);

                // Vérification immédiate
                var storedAnalysis = AnalysisDataStore.GetAnalysis(Context.ConnectionId);
                _logger.LogInformation(
                    $"[StartAnalysis_HUB_LOG] ✅ Analyse stockée pour ConnectionId: {Context.ConnectionId}. Analysis in DataStore is null: {storedAnalysis == null}. Actions if not null: {storedAnalysis?.Actions?.Count ?? -1}");

                // Mettre à jour _analysisActions pour l'interface utilisateur, seulement si des actions concrètes existent.
                if (result.Analysis?.Actions != null && result.Analysis.Actions.Any())
                {
                    var actionItems = result.Analysis.Actions.Select(a => new LegacyImportActionItem
                    {
                        RowIndex = 0,
                        ActionType = a.ActionType.ToString(),
                        Data = new Dictionary<string, string>
                        {
                            ["objectName"] = a.ObjectName,
                            ["path"] = a.Path,
                            ["message"] = a.Message
                        },
                        IsValid = true
                    }).ToList();

                    _analysisActions[Context.ConnectionId] = actionItems;
                    _logger.LogInformation(
                        $"[StartAnalysis] UI actions (_analysisActions) updated for ConnectionId {Context.ConnectionId}: {actionItems.Count} actions.");
                }
                else
                {
                    // Aucune action de la nouvelle analyse, ou result.Analysis est null.
                    // Nettoyer les actions précédemment affichées pour cette connexion pour éviter la confusion à l'UI.
                    _analysisActions.TryRemove(Context.ConnectionId, out _);
                    _logger.LogInformation(
                        $"[StartAnalysis] No actions in result.Analysis or result.Analysis is null. UI actions (_analysisActions) cleared for ConnectionId {Context.ConnectionId}. result.Analysis.Actions was null: {result.Analysis?.Actions == null}, or empty: {!result.Analysis?.Actions?.Any() ?? true}");
                }

                await _signalRService.SendCsvAnalysisCompleteAsync(Context.ConnectionId, result);
            }
            else
            {
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                    result.ErrorMessage ?? "Erreur lors de l'analyse des données du fichier");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de l'analyse des données du fichier");

            // Vérifier si la connexion est toujours active avant d'envoyer l'erreur
            if (!Context.ConnectionAborted.IsCancellationRequested)
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                    $"Erreur lors de l'analyse des données du fichier: {ex.Message}");
            else
                _logger.LogWarning(
                    $"StartAnalysis: Impossible d'envoyer l'erreur, connexion {Context.ConnectionId} fermée");
        }
    }

    public async Task StartImport(ImportOperationData importData)
    {
        _logger.LogInformation($"StartImport appelé par le client {Context.ConnectionId}");

        // S'assurer que la connexion est toujours active
        if (Context.ConnectionAborted.IsCancellationRequested)
        {
            _logger.LogWarning($"StartImport: Connexion {Context.ConnectionId} a été annulée");
            return;
        }

        if (importData == null)
        {
            _logger.LogWarning("Données d'import non fournies");
            await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, "Données d'import non fournies");
            return;
        }

        if (string.IsNullOrEmpty(importData.ConfigId))
        {
            _logger.LogWarning("ID de configuration non fourni");
            await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, "ID de configuration non fourni");
            return;
        }

        var spreadsheetData = FileDataStore.GetCsvData(Context.ConnectionId);

        if (spreadsheetData == null || spreadsheetData.Count == 0)
        {
            _logger.LogWarning("Aucune donnée de fichier trouvée pour l'import");
            await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                "Aucune donnée de fichier trouvée pour l'import. Veuillez d'abord uploader un fichier.");
            return;
        }

        ImportConfig importConfig;
        try
        {
            var savedConfigs = await _configService.GetSavedImportConfigs();
            var configEntry = savedConfigs.FirstOrDefault(c => c.Id == importData.ConfigId);
            if (configEntry == null)
            {
                _logger.LogWarning($"Configuration {importData.ConfigId} non trouvée pour l'import");
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                    $"Configuration {importData.ConfigId} non trouvée");
                return;
            }

            importConfig = configEntry.ConfigData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration pour l'import");
            await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                $"Erreur lors de la récupération de la configuration: {ex.Message}");
            return;
        }

        try
        {
            await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 10, "importing",
                "Début de l'import...");

            ImportResult result;

            if (importData.Actions != null && importData.Actions.Any())
            {
                var selectedActions = importData.Actions.Where(a => a.IsValid).ToList();
                _logger.LogInformation(
                    $"Utilisation des actions fournies par le client: {selectedActions.Count} actions sélectionnées sur {importData.Actions.Count} fournies.");

                if (selectedActions.Count == 0)
                {
                    _logger.LogWarning("Aucune action sélectionnée pour l'import");
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                        "Aucune action sélectionnée. Veuillez sélectionner au moins une action à exécuter.");
                    return;
                }

                await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 20, "importing",
                    $"Exécution de {selectedActions.Count} actions...");
                result = await _spreadsheetImportService.ExecuteImportFromActionsAsync(spreadsheetData, importConfig,
                    selectedActions, Context.ConnectionId);
            }
            else
            {
                _logger.LogInformation(
                    $"[StartImport] No client actions provided. ConnectionId: {Context.ConnectionId}. Attempting to use analysis from AnalysisDataStore.");

                AnalysisDataStore.LogCurrentState();
                _logger.LogInformation(
                    $"[StartImport] Tentative de récupération de l'analyse pour ConnectionId: {Context.ConnectionId}");

                var analysisToExecute = AnalysisDataStore.GetAnalysis(Context.ConnectionId);

                // Fallback vers la méthode legacy si aucune analyse trouvée avec connectionId
                if (analysisToExecute == null)
                {
                    _logger.LogWarning(
                        $"[StartImport] Aucune analyse trouvée pour ConnectionId {Context.ConnectionId}, tentative avec méthode legacy");
                    analysisToExecute = AnalysisDataStore.GetLatestAnalysis();
                    if (analysisToExecute != null)
                        _logger.LogInformation(
                            $"[StartImport] Analyse trouvée via méthode legacy avec {analysisToExecute.Actions?.Count ?? 0} actions");
                }

                if (analysisToExecute != null && analysisToExecute.Actions != null && analysisToExecute.Actions.Any())
                {
                    _logger.LogInformation(
                        $"[StartImport] Using analysis from AnalysisDataStore for ConnectionId {Context.ConnectionId}. It has {analysisToExecute.Actions.Count} actions. Executing import...");
                    await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 20, "importing",
                        $"Exécution de {analysisToExecute.Actions.Count} actions à partir de la dernière analyse stockée...");
                    result = await _spreadsheetImportService.ExecuteImportFromAnalysisAsync(analysisToExecute,
                        importConfig, Context.ConnectionId);
                }
                else
                {
                    var reason = "Raison inconnue";
                    if (analysisToExecute == null) reason = "Analysis from AnalysisDataStore is null";
                    else if (analysisToExecute.Actions == null)
                        reason = "AnalysisDataStore.GetLatestAnalysis().Actions is null";
                    else if (!analysisToExecute.Actions.Any())
                        reason = "AnalysisDataStore.GetLatestAnalysis().Actions is empty";

                    _logger.LogWarning(
                        $"[StartImport] Cannot proceed with import using analysis from AnalysisDataStore for ConnectionId {Context.ConnectionId}. Reason: {reason}. Retrieved analysis is null: {analysisToExecute == null}. Actions count if not null: {analysisToExecute?.Actions?.Count ?? -1}");
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                        $"Aucune analyse préalable avec des actions exécutables n'a été trouvée (Raison: {reason}). Veuillez d'abord analyser le fichier.");
                    result = new ImportResult
                    {
                        Success = false,
                        Message =
                            $"Aucune analyse stockée avec des actions exécutables disponible (Raison: {reason}). StartAnalysis doit être appelé en premier.",
                        TotalActions = 1,
                        ErrorCount = 1,
                        Results = new List<ImportActionResult>
                        {
                            new()
                            {
                                Success = false,
                                ActionType = ActionType.ERROR,
                                ObjectName = "Import",
                                Path = "",
                                Message = $"Aucune analyse préalable disponible: {reason}"
                            }
                        }
                    };
                }
            }

            _logger.LogInformation(
                $"[StartImport] Import terminé for ConnectionId {Context.ConnectionId}. Succès: {result.Success}, Réussis: {result.TotalSucceeded}, Échecs: {result.TotalFailed}, Détails: {result.Details}");
            await _signalRService.SendCsvImportCompleteAsync(Context.ConnectionId, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de l'exécution de l'import");

            // Vérifier si la connexion est toujours active avant d'envoyer l'erreur
            if (!Context.ConnectionAborted.IsCancellationRequested)
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId,
                    $"Erreur lors de l'exécution de l'import: {ex.Message}");
            else
                _logger.LogWarning(
                    $"StartImport: Impossible d'envoyer l'erreur, connexion {Context.ConnectionId} fermée");
        }
    }
}

public class ImportOperationData
{
    public string ConfigId { get; set; }
    public List<LegacyImportActionItem> Actions { get; set; }
}