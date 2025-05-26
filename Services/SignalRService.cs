using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using ADManagerAPI.Hubs;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using LogEntry = ADManagerAPI.Models.LogEntry;
using ModelLogLevel = ADManagerAPI.Models.LogLevel;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ADManagerAPI.Services
{
    /// <summary>
    /// Service qui gère les communications en temps réel via SignalR
    /// </summary>
    public class SignalRService : ISignalRService
    {
        private readonly IHubContext<CsvImportHub> _csvImportHubContext;
        private readonly IHubContext<NotificationHub> _notificationHubContext;
        private readonly ILogger<SignalRService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IConfigService _configService;
        
        // Dictionnaire pour stocker l'état de progression des opérations par connexion
        private static readonly ConcurrentDictionary<string, ImportProgress> _progressState = new();

        public SignalRService(
            IHubContext<CsvImportHub> csvImportHubContext,
            IHubContext<NotificationHub> notificationHubContext,
            ILogger<SignalRService> logger,
            IConfigService configService,
            IServiceScopeFactory serviceScopeFactory)
        {
            _csvImportHubContext = csvImportHubContext ?? throw new ArgumentNullException(nameof(csvImportHubContext));
            _notificationHubContext = notificationHubContext ?? throw new ArgumentNullException(nameof(notificationHubContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        }

        public async Task<bool> IsConnectedAsync()
        {
            // SignalR gère automatiquement les connexions, donc on peut supposer que le service est toujours connecté
            return await Task.FromResult(true);
        }

        public async Task SendCsvAnalysisProgressAsync(string connectionId, int progress, string status, string message, ImportAnalysis? analysis = null)
        {
            var progressData = new ImportProgress
            {
                Progress = progress,
                Status = status,
                Message = message,
                Analysis = analysis
            };
            
            // Stocker l'état de progression actuel
            _progressState[connectionId] = progressData;
            
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveProgress", progressData);
            
            // Journaliser la progression
            _logger.LogInformation($"Progression pour {connectionId}: {progress}% - {status} - {message}");
            
            // Envoyer également un log pour afficher dans l'interface
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveLog", new LogEntry
            {
                LevelText = "info",
                Level = ModelLogLevel.Information,
                Message = message
            });
        }
        
        public async Task SendCsvAnalysisCompleteAsync(string connectionId, CsvAnalysisResult result)
        {
            if (result == null)
            {
                _logger.LogWarning("Tentative d'envoi d'un résultat d'analyse nul");
                return;
            }
            
            // Envoyer un événement de progression à 100%
            await SendCsvAnalysisProgressAsync(connectionId, 100, "analyzed", "Analyse terminée avec succès", result.Analysis);
            
            // Envoyer l'événement spécifique d'analyse complète
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ANALYSIS_COMPLETE", new
            {
                Data = new
                {
                    csvHeaders = result.CsvHeaders,
                    tableData = result.TableData,
                    errors = result.Errors,
                    isValid = result.IsValid,
                    summary = result.Summary,
                    preview = result.PreviewData,
                    analysis = result.Analysis,
                    success = result.Success
                }
            });
            
            // Envoyer un log de succès
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveLog", new LogEntry
            {
                LevelText = "success",
                Level = ModelLogLevel.Information,
                Message = "Analyse terminée avec succès"
            });
        }
        
        public async Task SendCsvAnalysisErrorAsync(string connectionId, string errorMessage)
        {
            // Envoyer un événement de progression en erreur
            await SendCsvAnalysisProgressAsync(connectionId, 0, "error", errorMessage);
            
            // Envoyer l'événement spécifique d'erreur d'analyse
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ANALYSIS_ERROR", new
            {
                Data = new { Error = errorMessage }
            });
            
            // Envoyer un log d'erreur
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveLog", new LogEntry
            {
                LevelText = "error",
                Level = ModelLogLevel.Error,
                Message = errorMessage
            });
        }
        
        public async Task SendCsvImportCompleteAsync(string connectionId, ImportResult result)
        {
            if (result == null)
            {
                _logger.LogWarning("Tentative d'envoi d'un résultat d'import nul");
                return;
            }
            
            // Envoyer un événement de progression à 100%
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveProgress", new ImportProgress
            {
                Progress = 100,
                Status = "completed",
                Message = result.Success ? "Import terminé avec succès" : "Import terminé avec des erreurs",
                Result = result
            });
            
            // Envoyer l'événement spécifique d'import complet
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("IMPORT_COMPLETE", new
            {
                Data = result
            });
            
            // Envoyer un log de succès ou d'avertissement
            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveLog", new LogEntry
            {
                LevelText = result.Success ? "success" : "warning",
                Level = result.Success ? ModelLogLevel.Information : ModelLogLevel.Warning,
                Message = result.Success
                    ? $"Import terminé avec succès. {result.TotalSucceeded} objets traités avec succès."
                    : $"Import terminé avec des erreurs. {result.TotalFailed} erreurs sur {result.TotalProcessed} objets."
            });
        }

        public async Task SendMessageToConnectionAsync(string connectionId, object message)
        {
            _logger.LogInformation($"Envoi d'un message à la connexion {connectionId}");
            
            if (string.IsNullOrEmpty(connectionId))
            {
                _logger.LogWarning("ID de connexion non fourni pour l'envoi du message");
                return;
            }
            
            // Si le message est typé, l'envoyer au format approprié
            if (message is { } typedMessage)
            {
                var messageType = GetMessageType(typedMessage);
                _logger.LogInformation($"Type de message détecté: {messageType}");
                
                switch (messageType)
                {
                    case "ANALYSIS_COMPLETE":
                    case "ANALYSIS_ERROR":
                    case "IMPORT_COMPLETE":
                    case "IMPORT_ERROR":
                    case "IMPORT_PROGRESS":
                    case "ANALYSIS_PROGRESS":
                        await _csvImportHubContext.Clients.Client(connectionId).SendAsync(messageType, typedMessage);
                        break;
                        
                    case "log":
                        if (TryExtractLogEntry(typedMessage, out var logEntry))
                        {
                            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveLog", logEntry);
                        }
                        else
                        {
                            await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveMessage", typedMessage);
                        }
                        break;
                    
                    default:
                        await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveMessage", typedMessage);
                        break;
                }
            }
            else
            {
                // Message non typé, l'envoyer comme un message générique
                await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveMessage", message);
            }
        }

        public IServiceScope CreateScope()
        {
            return _serviceScopeFactory.CreateScope();
        }

        public async Task ProcessCsvUpload(string? connectionId, Stream fileStream, string fileName, ImportConfig config)
        {
            _logger.LogInformation($"Traitement de l'upload du fichier {fileName} pour la connexion {connectionId}");
            try
            {
                // Envoyer un log de début de traitement
                await _csvImportHubContext.Clients.Client(connectionId).SendAsync("ReceiveLog", new LogEntry
                {
                    LevelText = "info",
                    Message = $"Traitement du fichier {fileName}...",
                    Level = ModelLogLevel.Information
                });
                
                // Envoyer la progression initiale
                await SendCsvAnalysisProgressAsync(connectionId, 10, "processing", $"Traitement du fichier {fileName}...");
                
                // Valider le flux du fichier
                if (fileStream == null || fileStream.Length == 0)
                {
                    _logger.LogWarning("Flux de fichier vide ou null reçu");
                    await SendCsvAnalysisErrorAsync(connectionId, "Le fichier fourni est vide ou invalide.");
                    return;
                }
                
                // Mettre à jour la progression
                await SendCsvAnalysisProgressAsync(connectionId, 30, "processing", $"Analyse du fichier {fileName}...");
                
                // Créer un scope pour obtenir le service d'analyse CSV
                using var scope = _serviceScopeFactory.CreateScope();
                var csvManagerService = scope.ServiceProvider.GetRequiredService<ICsvManagerService>();
                
                // Appeler la méthode d'analyse du CsvManagerService
                var analysisResult = await csvManagerService.AnalyzeCsvContentAsync(fileStream, fileName, config);
                
                // Mettre à jour la progression après l'analyse
                await SendCsvAnalysisProgressAsync(connectionId, 70, "analyzing", "Analyse terminée, préparation des résultats...");
                
                if (analysisResult.Success)
                {
                    // Vérifier que TableData n'est pas null et que connectionId est valide
                    if (analysisResult.TableData != null && !string.IsNullOrEmpty(connectionId))
                    {
                        // Stocker les données CSV brutes parsées pour une utilisation ultérieure par le Hub
                        ADManagerAPI.Models.CsvDataStore.SetCsvData(analysisResult.TableData, connectionId);
                        _logger.LogInformation($"Données CSV brutes ({analysisResult.TableData.Count} lignes) de {fileName} stockées dans CsvDataStore pour connectionId: {connectionId}");
                    }
                    else
                    {
                        if (analysisResult.TableData == null)
                        {
                             _logger.LogWarning($"analysisResult.TableData est null après l'analyse du fichier {fileName} pour {connectionId}. Les données ne seront pas stockées.");
                        }
                        if (string.IsNullOrEmpty(connectionId))
                        {
                             _logger.LogWarning($"ConnectionId est null ou vide après l'analyse du fichier {fileName}. Les données ne seront pas stockées.");
                        }
                    }

                    _logger.LogInformation($"Analyse du fichier {fileName} réussie pour {connectionId}");
                    await SendCsvAnalysisCompleteAsync(connectionId, analysisResult);
                }
                else
                {
                    _logger.LogError($"Échec de l'analyse du fichier {fileName} pour {connectionId}: {analysisResult.ErrorMessage}");
                    await SendCsvAnalysisErrorAsync(connectionId, analysisResult.ErrorMessage ?? "Une erreur inconnue est survenue lors de l'analyse.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors du traitement de l'upload du fichier {fileName} pour la connexion {connectionId}");
                await SendCsvAnalysisErrorAsync(connectionId, $"Erreur lors du traitement du fichier {fileName}: {ex.Message}");
            }
        }

        public async Task SendNotificationAsync(string userId, string message)
        {
            _logger.LogInformation($"Envoi d'une notification à l'utilisateur {userId}: {message}");
            
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("ID d'utilisateur non fourni pour l'envoi de la notification");
                return;
            }
            
            try
            {
                await _notificationHubContext.Clients.User(userId).SendAsync("ReceiveNotification", new
                {
                    Message = message,
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
                
                _logger.LogInformation($"Notification envoyée à l'utilisateur {userId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors de l'envoi de la notification à l'utilisateur {userId}");
            }
        }

        public async Task BroadcastAsync(string message)
        {
            _logger.LogInformation($"Diffusion d'un message à tous les clients: {message}");
            
            try
            {
                await _csvImportHubContext.Clients.All.SendAsync("ReceiveMessage", message);
                _logger.LogInformation("Message diffusé avec succès");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la diffusion du message");
            }
        }

        private string GetMessageType(object message)
        {
            if (message == null) return "null";
            
            var type = message.GetType();
            
            if (type.Name.Contains("LogEntry") || type.Name.Contains("Log"))
                return "log";
                
            if (message is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
            {
                if (jsonElement.TryGetProperty("type", out var typeProperty))
                    return typeProperty.GetString() ?? "unknown";
            }
            
            return "message";
        }

        private bool TryExtractLogEntry(object message, out LogEntry logEntry)
        {
            logEntry = null;
            
            if (message is LogEntry entry)
            {
                logEntry = entry;
                return true;
            }
            
            try
            {
                if (message is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
                {
                    logEntry = new LogEntry();
                    
                    if (jsonElement.TryGetProperty("message", out var messageProperty))
                    {
                        logEntry.Message = messageProperty.GetString();
                    }
                    else if (jsonElement.TryGetProperty("Message", out messageProperty))
                    {
                        logEntry.Message = messageProperty.GetString();
                    }
                    
                    if (jsonElement.TryGetProperty("level", out var levelProperty))
                    {
                        logEntry.LevelText = levelProperty.GetString();
                        
                        // Convertir le niveau de log
                        if (levelProperty.ValueKind == JsonValueKind.String && 
                            Enum.TryParse<MsLogLevel>(levelProperty.GetString(), true, out var logLevel))
                        {
                            logEntry.Level = ConvertToModelLogLevel(logLevel);
                        }
                    }
                    else if (jsonElement.TryGetProperty("Level", out levelProperty))
                    {
                        logEntry.LevelText = levelProperty.GetString();
                        
                        // Convertir le niveau de log
                        if (levelProperty.ValueKind == JsonValueKind.String && 
                            Enum.TryParse<MsLogLevel>(levelProperty.GetString(), true, out var logLevel))
                        {
                            logEntry.Level = ConvertToModelLogLevel(logLevel);
                        }
                    }
                    else if (jsonElement.TryGetProperty("levelText", out var levelTextProperty))
                    {
                        logEntry.LevelText = levelTextProperty.GetString();
                        
                        // Convertir le niveau de log
                        if (levelTextProperty.ValueKind == JsonValueKind.String && 
                            Enum.TryParse<MsLogLevel>(levelTextProperty.GetString(), true, out var logLevel))
                        {
                            logEntry.Level = ConvertToModelLogLevel(logLevel);
                        }
                    }
                    else if (jsonElement.TryGetProperty("LevelText", out levelTextProperty))
                    {
                        logEntry.LevelText = levelTextProperty.GetString();
                        
                        // Convertir le niveau de log
                        if (levelTextProperty.ValueKind == JsonValueKind.String && 
                            Enum.TryParse<MsLogLevel>(levelTextProperty.GetString(), true, out var logLevel))
                        {
                            logEntry.Level = ConvertToModelLogLevel(logLevel);
                        }
                    }
                    
                    if (jsonElement.TryGetProperty("timestamp", out var timestampProperty))
                    {
                        string timestampStr = timestampProperty.GetString();
                        if (!string.IsNullOrEmpty(timestampStr) && DateTime.TryParse(timestampStr, out var timestamp))
                        {
                            logEntry.Timestamp = timestamp;
                        }
                        else
                        {
                            logEntry.Timestamp = DateTime.Now;
                        }
                    }
                    else if (jsonElement.TryGetProperty("Timestamp", out timestampProperty))
                    {
                        string timestampStr = timestampProperty.GetString();
                        if (!string.IsNullOrEmpty(timestampStr) && DateTime.TryParse(timestampStr, out var timestamp))
                        {
                            logEntry.Timestamp = timestamp;
                        }
                        else
                        {
                            logEntry.Timestamp = DateTime.Now;
                        }
                    }
                    else
                    {
                        logEntry.Timestamp = DateTime.Now;
                    }
                    
                    if (string.IsNullOrEmpty(logEntry.Message))
                    {
                        return false;
                    }
                    
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erreur lors de l'extraction du LogEntry");
            }
            
            return false;
        }

        private ModelLogLevel ConvertToModelLogLevel(MsLogLevel level)
        {
            return level switch
            {
                MsLogLevel.Critical => ModelLogLevel.Critical,
                MsLogLevel.Error => ModelLogLevel.Error,
                MsLogLevel.Warning => ModelLogLevel.Warning,
                MsLogLevel.Information => ModelLogLevel.Information,
                MsLogLevel.Debug => ModelLogLevel.Debug,
                MsLogLevel.Trace => ModelLogLevel.Trace,
                _ => ModelLogLevel.Information
            };
        }

        private LogEntry CreateLogEntry(DateTime timestamp, MsLogLevel level, string message, string? category = null, Dictionary<string, object>? data = null)
        {
            return new LogEntry
            {
                Timestamp = timestamp,
                Level = ConvertToModelLogLevel(level),
                LevelText = level.ToString().ToLowerInvariant(),
                Message = message,
                Category = category,
                Data = data
            };
        }

        public async Task NotifyUnsavedChanges(string connectionId, string message, MsLogLevel logLevel = MsLogLevel.Information)
        {
            DateTime now = DateTime.Now;
            
            // Créer une entrée de log pour le client
            var logEntry = CreateLogEntry(now, logLevel, message);
            
            // Envoyer au client
            await _csvImportHubContext.Clients
                .Client(connectionId)
                .SendAsync("ReceiveLog", logEntry);
        }
    }
} 