using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ADManagerAPI.Models;
using ADManagerAPI.Services;
using ADManagerAPI.Services.Utilities;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using ADManagerAPI.Hubs;
using System.Text;

namespace ADManagerAPI.Controllers
{
    [ApiController]
    [Route("api/import")]
    [Authorize]
    public class FileImportController : ControllerBase
    {
        private readonly ILdapService _ldapService;
        private readonly ILogService _logService;
        private readonly IConfigService _configService;
        private readonly ISignalRService _signalRService;
        private readonly ICsvManagerService _csvManagerService;
        private readonly ILogger<FileImportController> _logger;
        private readonly IHubContext<CsvImportHub> _csvImportHubContext;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public FileImportController(
            ILdapService ldapService,
            ILogService logService,
            IConfigService configService,
            ISignalRService signalRService,
            ICsvManagerService csvManagerService,
            ILogger<FileImportController> logger,
            IHubContext<CsvImportHub> csvImportHubContext,
            IServiceScopeFactory serviceScopeFactory)
        {
            _ldapService = ldapService;
            _logService = logService;
            _configService = configService;
            _signalRService = signalRService;
            _csvManagerService = csvManagerService;
            _logger = logger;
            _csvImportHubContext = csvImportHubContext;
            _serviceScopeFactory = serviceScopeFactory;
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

                if (configDto == null)
                {
                    _logService.Log("IMPORT", "Le DTO reçu est null");
                    return BadRequest(new { error = "Aucune donnée reçue" });
                }

                _logService.Log("IMPORT",
                    $"DTO reçu: Name={configDto.Name}, ObjectType={configDto.ConfigData?.ObjectTypeStr}");

                if (string.IsNullOrEmpty(configDto.Name))
                {
                    return BadRequest(new { error = "Le nom de la configuration est requis." });
                }

                if (configDto.ConfigData == null)
                {
                    return BadRequest(new { error = "Les données de configuration sont requises." });
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
                    stack = ex.StackTrace,
                    innerException = ex.InnerException?.Message
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

        [HttpPost("upload-file")]
        public async Task<IActionResult> UploadFile(IFormFile file, [FromForm] string configId, [FromForm] string? connectionId = null)
        {
            try
            {
                _logService.Log("IMPORT", $"Début de l'upload du fichier {file?.FileName} via HTTP avec configId: {configId}");
                
                if (file == null)
                {
                    _logService.Log("IMPORT", "Échec de l'upload: le fichier est null");
                    return BadRequest(new { error = "Le fichier est null" });
                }
                
                if (file.Length == 0)
                {
                    _logService.Log("IMPORT", "Échec de l'upload: le fichier est vide (taille 0)");
                    return BadRequest(new { error = "Le fichier est vide" });
                }
                
                _logService.Log("IMPORT", $"Fichier reçu: {file.FileName}, taille: {file.Length} octets, type: {file.ContentType}");

                _logService.Log("IMPORT", $"Recherche de la configuration avec l'ID: {configId}");
                ImportConfig config;
                var savedConfigs = await _configService.GetSavedImportConfigs();
                var savedConfig = savedConfigs.FirstOrDefault(c => c.Id == configId);
                if (savedConfig == null)
                {
                    _logService.Log("IMPORT", $"Configuration non trouvée: {configId}");
                    return BadRequest(new { error = "Configuration non trouvée" });
                }
                
                _logService.Log("IMPORT", $"Configuration trouvée: {savedConfig.Name}");
                config = savedConfig.ConfigData;
                
                _logService.Log("IMPORT", "Validation de la configuration...");
                config = ImportConfigHelpers.EnsureValidConfig(config, _logger);
                
                if (!string.IsNullOrEmpty(connectionId))
                {
                    _logService.Log("IMPORT", $"ConnectionId fourni: {connectionId}, traitement du fichier {file.FileName} en cours...");
                    
                    // Attend que le traitement du fichier soit terminé avant de continuer.
                    await _signalRService.ProcessCsvUpload(connectionId, file.OpenReadStream(), file.FileName, config);
                    _logService.Log("IMPORT", $"Traitement du fichier {file.FileName} terminé pour la connexion {connectionId}. Le client peut maintenant appeler StartAnalysis.");
                    
                    // La ligne suivante est supprimée car le client (TypeScript) est responsable d'appeler StartAnalysis sur le Hub.
                    // await _csvImportHubContext.Clients.Client(connectionId).SendAsync("StartAnalysis", configId);
                }
                else
                {
                    // Gérer le cas où connectionId est null ou vide si nécessaire, 
                    // par exemple, effectuer une analyse synchrone ou retourner une erreur.
                    _logService.Log("WARN", "ConnectionId non fourni pour l'upload. L'analyse via SignalR ne sera pas automatiquement déclenchée par le serveur après l'upload.");
                    // Pour l'instant, on suppose que le client gérera l'appel à StartAnalysis séparément ou que ce cas n'est pas attendu.
                    // Si une analyse doit quand même se produire, il faudrait appeler CsvManagerService.AnalyzeCsvContentAsync ici directement.
                    // var analysisResult = await _csvManagerService.AnalyzeCsvContentAsync(file.OpenReadStream(), file.FileName, config);
                    // Et ensuite retourner un résultat basé sur analysisResult.
                }

                return Ok(new { 
                    message = $"Upload du fichier {file.FileName} reçu, l'analyse va démarrer.", 
                    isAnalysisStarted = !string.IsNullOrEmpty(connectionId)
                });
            }
            catch (Exception ex)
            {
                _logService.Log("IMPORT", $"Erreur lors de l'upload du fichier: {ex.Message}");
                _logService.Log("IMPORT", $"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _logService.Log("IMPORT", $"Inner exception: {ex.InnerException.Message}");
                }
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("system/check-websocket")]
        [AllowAnonymous] // Ou [Authorize] si l'utilisateur doit être authentifié pour vérifier
        public IActionResult CheckWebSocketAvailability()
        {
            // On pourrait ajouter une logique plus complexe ici si nécessaire,
            // par exemple, vérifier si le service SignalR est réellement disponible.
            // Pour l'instant, un simple OK suffit pour indiquer que le endpoint existe.
            _logService.Log("SYSTEM", "Vérification de la disponibilité WebSocket demandée.");
            return Ok(new { message = "WebSocket endpoint available" });
        }

        [HttpGet("system/check-signalr")]
        [AllowAnonymous] // Ou [Authorize] si nécessaire
        public IActionResult CheckSignalRAvailability()
        {
            // Idéalement, vérifier si le hub SignalR est accessible
            // Pour l'instant, retourner OK indique que l'API est au courant de SignalR.
            _logService.Log("SYSTEM", "Vérification de la disponibilité SignalR demandée.");
            return Ok(new { message = "SignalR endpoint potentially available" });
        }
    }
} 