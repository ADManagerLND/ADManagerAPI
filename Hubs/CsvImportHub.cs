using Microsoft.AspNetCore.SignalR;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using System.Collections.Concurrent;
using ModelLogLevel = ADManagerAPI.Models.LogLevel;

namespace ADManagerAPI.Hubs
{
    public class CsvImportHub : Hub
    {
        private readonly ILogger<CsvImportHub> _logger;
        private readonly ICsvManagerService _csvManagerService;
        private readonly IConfigService _configService;
        private readonly ISignalRService _signalRService;
        private static readonly ConcurrentDictionary<string, List<LegacyImportActionItem>> _analysisActions = new();
        private static readonly ConcurrentDictionary<string, List<Dictionary<string, string>>> _csvData = new();

        public CsvImportHub(
            ILogger<CsvImportHub> logger,
            ICsvManagerService csvManagerService,
            IConfigService configService,
            ISignalRService signalRService)
        {
            _logger = logger;
            _csvManagerService = csvManagerService;
            _configService = configService;
            _signalRService = signalRService;
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"Client connecté au CsvImportHub: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            _logger.LogInformation($"Client déconnecté du CsvImportHub: {Context.ConnectionId}");

            _analysisActions.TryRemove(Context.ConnectionId, out _);
            _csvData.TryRemove(Context.ConnectionId, out _);
            
            ADManagerAPI.Models.CsvDataStore.ClearCsvData(Context.ConnectionId);
            
            await base.OnDisconnectedAsync(exception);
        }

        /*public async Task UploadCsv(string csvContent, string configId)
        {
            _logger.LogInformation($"UploadCsv appelé par le client {Context.ConnectionId} avec configId: {configId}");
            
            if (string.IsNullOrWhiteSpace(csvContent))
            {
                _logger.LogWarning("Contenu CSV vide reçu");
                await Clients.Caller.SendAsync("ReceiveLog", new LogEntry
                {
                    LevelText = "error",
                    Level = ModelLogLevel.Error,
                    Message = "Le contenu CSV est vide"
                });
                return;
            }
            
            await Clients.Caller.SendAsync("ReceiveLog", new LogEntry
            {
                LevelText = "info",
                Level = ModelLogLevel.Information,
                Message = "Traitement du fichier CSV..."
            });
            
            try
            {
                ImportConfig config;
                if (string.IsNullOrEmpty(configId))
                {
                    _logger.LogWarning("Aucun ID de configuration fourni, utilisation d'une configuration par défaut");
                    config = new ImportConfig
                    {
                        DefaultOU = "DC=domain,DC=local",
                        CsvDelimiter = ';'
                    };
                }
                else
                {
                    var savedConfigs = await _configService.GetSavedImportConfigs();
                    var importConfig = savedConfigs.FirstOrDefault(c => c.Id == configId);
                    if (importConfig == null)
                    {
                        _logger.LogWarning($"Configuration {configId} non trouvée");
                        await Clients.Caller.SendAsync("ReceiveLog", new LogEntry
                        {
                            LevelText = "error",
                            Level = ModelLogLevel.Error,
                            Message = $"Configuration {configId} non trouvée"
                        });
                        return;
                    }
                    config = importConfig.ConfigData;
                }
   
                var result = await _csvManagerService.AnalyzeCsvContentAsync(csvContent, config);
                
                if (result.Success && result.TableData != null)
                {
                    _csvData[Context.ConnectionId] = result.TableData;
                    
                    ADManagerAPI.Models.CsvDataStore.SetCsvData(result.TableData, Context.ConnectionId);
                    
                    _logger.LogInformation($"Données CSV stockées pour la connexion {Context.ConnectionId}: {result.TableData.Count} lignes");
                    
                    await Clients.Caller.SendAsync("upload-csv-result", new { 
                        message = "Fichier CSV uploadé et stocké en mémoire",
                        rowCount = result.TableData.Count,
                        isAnalysisStarted = false
                    });
                }
                else
                {
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, result.ErrorMessage ?? "Erreur lors de l'analyse du fichier CSV");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du traitement du fichier CSV");
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, $"Erreur lors du traitement du fichier CSV: {ex.Message}");
            }
        }*/
        
        public async Task StartAnalysis(string configId)
        {
            _logger.LogInformation($"StartAnalysis appelé par le client {Context.ConnectionId} avec configId: {configId}");
            
            var csvData = CsvDataStore.GetCsvData(Context.ConnectionId);
          
            if (csvData == null || csvData.Count == 0)
            {
                csvData = CsvDataStore.GetCsvData();
                
                if (csvData == null || csvData.Count == 0)
                {
                    _logger.LogWarning($"Aucune donnée CSV trouvée pour la connexion {Context.ConnectionId}");
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, "Aucune donnée CSV trouvée. Veuillez d'abord uploader un fichier CSV.");
                    return;
                }
                else
                {
                    _logger.LogInformation($"Données CSV trouvées avec la clé par défaut, les associer à la connexion {Context.ConnectionId}");
                    CsvDataStore.SetCsvData(csvData, Context.ConnectionId);
                }
            }
            
            _logger.LogInformation($"Données CSV trouvées pour la connexion {Context.ConnectionId}: {csvData.Count} lignes");
            
            try
            {
                await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 10, "analyzing", "Analyse des données CSV...");
                
                ImportConfig config;
                if (string.IsNullOrEmpty(configId))
                {
                    _logger.LogWarning("Aucun ID de configuration fourni pour l'analyse, utilisation d'une configuration par défaut");
                    config = new ImportConfig
                    {
                        DefaultOU = "DC=domain,DC=local",
                        CsvDelimiter = ';'
                    };
                }
                else
                {
                    var savedConfigs = await _configService.GetSavedImportConfigs();
                    var importConfig = savedConfigs.FirstOrDefault(c => c.Id == configId);
                    if (importConfig == null)
                    {
                        _logger.LogWarning($"Configuration {configId} non trouvée pour l'analyse");
                        await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, $"Configuration {configId} non trouvée");
                        return;
                    }
                    config = importConfig.ConfigData;
                }
                
                await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 30, "analyzing", "Analyse des colonnes et validation des données...");
                
                var result = await _csvManagerService.AnalyzeCsvDataAsync(csvData, config);
                
                if (result.Success)
                {
                    if (result.Analysis?.Actions != null)
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
                        _logger.LogInformation($"Actions d'analyse stockées pour la connexion {Context.ConnectionId}: {actionItems.Count} actions");
                    }
                    
                    await _signalRService.SendCsvAnalysisCompleteAsync(Context.ConnectionId, result);
                }
                else
                {
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, result.ErrorMessage ?? "Erreur lors de l'analyse des données CSV");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de l'analyse des données CSV");
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, $"Erreur lors de l'analyse des données CSV: {ex.Message}");
            }
        }
        
        public async Task StartImport(ImportOperationData importData)
        {
            _logger.LogInformation($"StartImport appelé par le client {Context.ConnectionId}");
            
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
            
            var csvData = CsvDataStore.GetCsvData(Context.ConnectionId);
            
            if (csvData == null || csvData.Count == 0)
            {
                _logger.LogWarning("Aucune donnée CSV trouvée pour l'import");
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, "Aucune donnée CSV trouvée pour l'import. Veuillez d'abord uploader un fichier CSV.");
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
                    await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, $"Configuration {importData.ConfigId} non trouvée");
                    return;
                }
                importConfig = configEntry.ConfigData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération de la configuration pour l'import");
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, $"Erreur lors de la récupération de la configuration: {ex.Message}");
                return;
            }
            
            try
            {
                await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 10, "importing", "Début de l'import...");
                
                ImportResult result;
                
                // Si des actions sont fournies par le client, utiliser ExecuteImportFromActionsAsync
                if (importData.Actions != null && importData.Actions.Any())
                {
                    var selectedActions = importData.Actions.Where(a => a.IsValid).ToList();
                    _logger.LogInformation($"Utilisation des actions fournies par le client: {selectedActions.Count} actions sélectionnées");
                    
                    if (selectedActions.Count == 0)
                    {
                        _logger.LogWarning("Aucune action sélectionnée pour l'import");
                        await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, "Aucune action sélectionnée. Veuillez sélectionner au moins une action à exécuter.");
                        return;
                    }
                    
                    await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 20, "importing", $"Exécution de {selectedActions.Count} actions...");
                    result = await _csvManagerService.ExecuteImportFromActionsAsync(csvData, importConfig, selectedActions, Context.ConnectionId);
                }
                // Si aucune action n'est fournie, utiliser la méthode ExecuteImportFromDataAsync pour l'import complet
                else
                {
                    _logger.LogInformation("Aucune action fournie, utilisation de ExecuteImportFromDataAsync pour un import complet");
                    await _signalRService.SendCsvAnalysisProgressAsync(Context.ConnectionId, 20, "importing", "Analyse et exécution de l'import complet...");
                    result = await _csvManagerService.ExecuteImportFromDataAsync(csvData, importConfig, Context.ConnectionId);
                }
                
                _logger.LogInformation($"Import terminé avec succès: {result.TotalSucceeded} opérations réussies, {result.TotalFailed} échecs");
                await _signalRService.SendCsvImportCompleteAsync(Context.ConnectionId, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de l'exécution de l'import");
                await _signalRService.SendCsvAnalysisErrorAsync(Context.ConnectionId, $"Erreur lors de l'exécution de l'import: {ex.Message}");
            }
        }
    }
    
    public class ImportOperationData
    {
        public string ConfigId { get; set; }
        public List<LegacyImportActionItem> Actions { get; set; }
    }
} 