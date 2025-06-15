using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Services.Parse;

namespace ADManagerAPI.Controllers
{
    [ApiController]
    [Route("api/import")]
    [Authorize]
    [RequestSizeLimit(100 * 1024 * 1024)] // Autorise les fichiers jusqu'à 100 Mo
    public class FileImportController : ControllerBase
    {
        private readonly ILdapService _ldapService;
        private readonly ILogService _logService;
        private readonly IConfigService _configService;
        private readonly ISignalRService _signalRService;
        private readonly ILogger<FileImportController> _logger;
        private readonly ISpreadsheetImportService _importService;

        public FileImportController(
            ILdapService ldapService,
            ILogService logService,
            IConfigService configService,
            ISignalRService signalRService,
            ILogger<FileImportController> logger,
            ISpreadsheetImportService importService)
        {
            _ldapService = ldapService;
            _logService = logService;
            _configService = configService;
            _signalRService = signalRService;
            _logger = logger;
            _importService = importService;
        }


        [HttpGet("configs")]
        public async Task<IActionResult> GetSavedConfigs()
        {
            try
            {
                _logService.Log("IMPORT", "Récupération des configurations sauvegardées");
                var configs = await _configService.GetSavedImportConfigs();
                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération des configurations d'import");
                _logService.Log("IMPORT", $"Erreur lors de la récupération des configurations: {ex.Message}");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("configs")]
        [Consumes("application/json")]
        public async Task<IActionResult> SaveConfig([FromBody] SavedImportConfigDto configDto)
        {
            try
            {
                _logService.Log("IMPORT", "Début du traitement de la requête SaveConfig avec DTO");

               var validationResult = ValidateConfigDto(configDto);
                if (!validationResult.IsValid)
                {
                    return BadRequest(new { error = validationResult.ErrorMessage });
                }

                var configData = configDto.ToSavedImportConfig();

                if (string.IsNullOrEmpty(configData.Id))
                {
                    configData.Id = Guid.NewGuid().ToString();
                }

                configData.CreatedBy = User.Identity?.Name ?? "Système";
                configData.CreatedAt = DateTime.Now;

                _logService.Log("IMPORT", $"Configuration convertie, tentative de sauvegarde");
                var result = await _configService.SaveImportConfig(configData);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logService.Log("IMPORT", $"Erreur lors de la sauvegarde de la configuration: {ex.Message}");
                return BadRequest(new
                {
                    error = ex.Message,
                    details = ex.StackTrace
                });
            }
        }

       

        [HttpDelete("configs/{id}")]
        public async Task<IActionResult> DeleteConfig(string id)
        {
            try
            {
                _logService.Log("IMPORT", $"Suppression de la configuration: {id}");

                if (string.IsNullOrEmpty(id))
                {
                    return BadRequest(new { error = "L'identifiant de la configuration est requis." });
                }

                var configs = await _configService.GetSavedImportConfigs();

                var configToDelete = configs.FirstOrDefault(c => c.Id == id);
                if (configToDelete == null)
                {
                    return NotFound(new { error = $"Configuration non trouvée: {id}" });
                }

                await _configService.DeleteImportConfig(id);

                return Ok(new { message = "Configuration supprimée avec succès" });
            }
            catch (Exception ex)
            {
                _logService.Log("IMPORT", $"Erreur lors de la suppression de la configuration: {ex.Message}");
                return BadRequest(new { error = ex.Message });
            }
        }


        [Authorize]                                       
        [DisableRequestSizeLimit]                        
        [RequestSizeLimit(100 * 1024 * 1024)]             
        [Consumes("multipart/form-data")]                 
        [HttpPost("upload-file")]
        public async Task<IActionResult> UploadFile(
            [FromForm] IFormFile file,          
            [FromForm] string configId,
            [FromForm] string connectionId)
        {
            try
            {
  
                if (file == null)
                {
                     return BadRequest(new { error = "Aucun fichier fourni" });
                }
                
                 var (isValidConfig, configErrorMessage, config) = await GetAndValidateConfig(configId);
                if (!isValidConfig || config == null)
                {
                    _logger.LogWarning($"⚠️ [FileImportController] Configuration invalide ou manquante: {configErrorMessage}");
                    return BadRequest(new { error = configErrorMessage });
                }

                // Lancer le traitement du fichier par le service d'importation
                await _signalRService.ProcessCsvUpload(
                    connectionId, 
                    file.OpenReadStream(), 
                    file.FileName, 
                    config.ConfigData
                );
                
                return Ok(new { 
                    message = "Fichier reçu et analyse lancée", 
                    fileName = file.FileName, 
                    fileSize = file.Length, 
                    timestamp = DateTime.Now,
                    analysisInitiated = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [FileImportController] Erreur lors de l\'upload du fichier");
                _logService.Log("IMPORT", $"Erreur lors de l\'upload du fichier: {ex.Message}");
                return StatusCode(500, new { error = "Erreur interne du serveur lors de l\'upload du fichier", details = ex.Message });
            }
        }



        [HttpGet("system/check-websocket")]
        [Authorize]
        public async Task<IActionResult> CheckWebSocketAvailability()
        {
            _logService.Log("SYSTEM", "Vérification de la disponibilité de WebSocket via SignalR.");
            bool isConnected = await _signalRService.IsConnectedAsync(); // Correction ici
            if (isConnected)
            {
                return Ok(new { status = "Connecté", message = "SignalR (WebSocket) semble fonctionner." });
            }
            else
            {
                return StatusCode(503, new { status = "Non connecté", message = "SignalR (WebSocket) ne semble pas connecté." });
            }
        }

        [HttpGet("system/check-signalr")]
        [Authorize]
        public async Task<IActionResult> CheckSignalRAvailability()
        {
            _logService.Log("SYSTEM", "Vérification de la disponibilité de SignalR.");
            bool isConnected = await _signalRService.IsConnectedAsync(); // Correction ici
            if (isConnected)
            {
                return Ok(new { status = "Connecté", message = "SignalR semble fonctionner." });
            }
            else
            {
                return StatusCode(503, new { status = "Non connecté", message = "SignalR ne semble pas connecté." });
            }
        }



        // --- Méthodes d'aide privées ---
        private (bool IsValid, string ErrorMessage) ValidateConfigDto(SavedImportConfigDto configDto)
        {
            _logger.LogInformation($"[ValidateConfigDto] Début de la validation pour {configDto.Name}");
            if (string.IsNullOrEmpty(configDto.Name))
            {
                _logger.LogWarning("[ValidateConfigDto] Nom de configuration manquant.");
                return (false, "Le nom de la configuration est requis.");
            }

            if (configDto.ConfigData == null)
            {
                _logger.LogWarning("[ValidateConfigDto] Données de configuration (ConfigData) manquantes.");
                return (false, "Les données de configuration sont requises.");
            }
            
            if (configDto.ConfigData.HeaderMapping == null || !configDto.ConfigData.HeaderMapping.Any())
            {
                _logger.LogWarning("[ValidateConfigDto] Mappages d'en-têtes manquants.");
                return (false, "Au moins un mappage d'en-tête est requis.");
            }

            _logger.LogInformation("[ValidateConfigDto] Validation de la configuration réussie.");
            return (true, string.Empty);
        }

        private (bool IsValid, string ErrorMessage) ValidateUploadedFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return (false, "Aucun fichier n'a été sélectionné ou le fichier est vide.");
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension != ".csv" && extension != ".xlsx")
            {
                return (false, "Seuls les fichiers CSV ou XLSX sont autorisés.");
            }
            return (true, string.Empty);
        }

        private async Task<(bool IsValid, string ErrorMessage, SavedImportConfig Config)> GetAndValidateConfig(string configId)
        {
            if (string.IsNullOrEmpty(configId))
            {
                return (false, "L'ID de configuration est manquant.", null);
            }

            var configs = await _configService.GetSavedImportConfigs();
            var config = configs.FirstOrDefault(c => c.Id == configId);

            if (config == null)
            {
                return (false, $"Configuration avec l'ID '{configId}' non trouvée.", null);
            }
            return (true, string.Empty, config);
        }
    }
} 