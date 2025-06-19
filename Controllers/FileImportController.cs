using System.Text.Json;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Services.Parse;
using ADManagerAPI.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ADManagerAPI.Controllers;

[ApiController]
[Route("api/import")]
[Authorize]
[RequestSizeLimit(100 * 1024 * 1024)] // Autorise les fichiers jusqu'à 100 Mo
public class FileImportController : ControllerBase
{
    private readonly IConfigService _configService;
    private readonly ISpreadsheetImportService _importService;
    private readonly ILdapService _ldapService;
    private readonly ILogger<FileImportController> _logger;
    private readonly ILogService _logService;
    private readonly ISignalRService _signalRService;
    private readonly IServiceProvider _serviceProvider;

    public FileImportController(
        ILdapService ldapService,
        ILogService logService,
        IConfigService configService,
        ISignalRService signalRService,
        ILogger<FileImportController> logger,
        ISpreadsheetImportService importService,
        IServiceProvider serviceProvider)
    {
        _ldapService = ldapService;
        _logService = logService;
        _configService = configService;
        _signalRService = signalRService;
        _logger = logger;
        _importService = importService;
        _serviceProvider = serviceProvider;
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

    [HttpGet("configs/{id}")]
    public async Task<IActionResult> GetSavedConfig(string id)
    {
        try
        {
            var configs = await _configService.GetSavedImportConfigs();
            var config = configs.FirstOrDefault(c => c.Id == id);

            if (config == null)
                return NotFound(new { error = $"Configuration {id} non trouvée" });

            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration {Id}", id);
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
            if (!validationResult.IsValid) return BadRequest(new { error = validationResult.ErrorMessage });

            var configData = configDto.ToSavedImportConfig();

            if (string.IsNullOrEmpty(configData.Id)) configData.Id = Guid.NewGuid().ToString();

            configData.CreatedBy = User.Identity?.Name ?? "Système";
            configData.CreatedAt = DateTime.Now;

            _logService.Log("IMPORT", "Configuration convertie, tentative de sauvegarde");
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
                return BadRequest(new { error = "L'identifiant de la configuration est requis." });

            var configs = await _configService.GetSavedImportConfigs();

            var configToDelete = configs.FirstOrDefault(c => c.Id == id);
            if (configToDelete == null) return NotFound(new { error = $"Configuration non trouvée: {id}" });

            await _configService.DeleteImportConfig(id);

            return Ok(new { message = "Configuration supprimée avec succès" });
        }
        catch (Exception ex)
        {
            _logService.Log("IMPORT", $"Erreur lors de la suppression de la configuration: {ex.Message}");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("configs/{id}/duplicate")]
    public async Task<IActionResult> DuplicateConfig(string id, [FromBody] DuplicateConfigRequest request)
    {
        try
        {
            var configs = await _configService.GetSavedImportConfigs();
            var originalConfig = configs.FirstOrDefault(c => c.Id == id);

            if (originalConfig == null)
                return NotFound(new { error = $"Configuration {id} non trouvée" });

            var duplicatedConfig = new SavedImportConfig
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.Name ?? $"{originalConfig.Name} - Copie",
                Description = request.Description ?? $"Copie de {originalConfig.Description}",
                CreatedBy = User.Identity?.Name ?? "Système",
                CreatedAt = DateTime.Now,
                ConfigData = originalConfig.ConfigData,
                Category = originalConfig.Category,
                IsEnabled = true
            };

            var savedConfig = await _configService.SaveImportConfig(duplicatedConfig);
            return Ok(savedConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la duplication de la configuration {Id}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("templates")]
    public async Task<IActionResult> GetConfigTemplates()
    {
        try
        {
            var templates = new List<ImportConfigTemplate>
            {
                GetLyceeStudentsBasicTemplate(),
                GetLyceeStudentsTeamsTemplate(),
                GetEnterpriseUsersTemplate()
            };

            return Ok(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des templates");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("templates/{templateId}")]
    public async Task<IActionResult> CreateFromTemplate(string templateId, [FromBody] CreateFromTemplateRequest request)
    {
        try
        {
            var template = GetTemplateById(templateId);
            if (template == null)
                return NotFound(new { error = $"Template {templateId} non trouvé" });

            var newConfig = new SavedImportConfig
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.Name,
                Description = request.Description ?? template.Description,
                CreatedBy = User.Identity?.Name ?? "Système",
                CreatedAt = DateTime.Now,
                ConfigData = template.ConfigData,
                Category = template.Category,
                IsEnabled = true
            };

            var savedConfig = await _configService.SaveImportConfig(newConfig);
            return Ok(savedConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la création depuis le template {TemplateId}", templateId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("configs/load-lycee-optimized")]
    public async Task<IActionResult> LoadLyceeOptimizedConfig()
    {
        try
        {
            var configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config",
                "lycee-import-optimized-2025.json");

            if (!System.IO.File.Exists(configPath))
                return NotFound(new { error = "Configuration optimisée du Lycée Notre-Dame non trouvée" });

            var json = await System.IO.File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize<SavedImportConfig>(json);

            if (config == null) return BadRequest(new { error = "Impossible de désérialiser la configuration" });

            // Vérifier si la configuration existe déjà
            var existingConfigs = await _configService.GetSavedImportConfigs();
            var existingConfig = existingConfigs.FirstOrDefault(c => c.Id == config.Id);

            if (existingConfig != null)
                return Ok(new { message = "Configuration déjà présente", config = existingConfig });

            // Ajouter la configuration
            config.CreatedBy = User.Identity?.Name ?? "Système";
            config.CreatedAt = DateTime.Now;

            var savedConfig = await _configService.SaveImportConfig(config);

            _logService.Log("IMPORT_CONFIG", "Configuration optimisée du Lycée Notre-Dame chargée");

            return Ok(new
            {
                message = "Configuration optimisée du Lycée Notre-Dame chargée avec succès",
                config = savedConfig
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors du chargement de la configuration optimisée");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("configs/validate")]
    public async Task<IActionResult> ValidateConfig([FromBody] SavedImportConfig config)
    {
        try
        {
            var result = new ConfigValidationResult();

            // Validation des champs obligatoires
            if (string.IsNullOrEmpty(config.Name))
                result.Errors.Add("Le nom de la configuration est requis");

            if (string.IsNullOrEmpty(config.ConfigData?.DefaultOU))
                result.Errors.Add("L'OU par défaut est requis");

            if (config.ConfigData?.HeaderMapping == null || !config.ConfigData.HeaderMapping.Any())
                result.Errors.Add("Au moins un mappage d'en-tête est requis");

            // Validation du format de l'OU
            if (!string.IsNullOrEmpty(config.ConfigData?.DefaultOU) &&
                !config.ConfigData.DefaultOU.StartsWith("OU=") &&
                !config.ConfigData.DefaultOU.StartsWith("CN="))
                result.Warnings.Add("Le format de l'OU semble incorrect (doit commencer par OU= ou CN=)");

            // Validation Teams
            if (config.ConfigData?.TeamsIntegration?.Enabled == true)
                if (string.IsNullOrEmpty(config.ConfigData.TeamsIntegration.DefaultTeacherUserId))
                    result.Warnings.Add("ID d'enseignant par défaut manquant pour l'intégration Teams");

            result.IsValid = !result.Errors.Any();

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la validation de la configuration");
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [DisableRequestSizeLimit]
    [RequestSizeLimit(100 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [HttpPost("upload-file")]
    public async Task<IActionResult> UploadFile(
        IFormFile file,
        string configId,
        string connectionId)
    {
        try
        {
            if (file == null) return BadRequest(new { error = "Aucun fichier fourni" });

            var (isValidConfig, configErrorMessage, config) = await GetAndValidateConfig(configId);
            if (!isValidConfig || config == null)
            {
                _logger.LogWarning(
                    $"⚠️ [FileImportController] Configuration invalide ou manquante: {configErrorMessage}");
                return BadRequest(new { error = configErrorMessage });
            }

            // Lancer le traitement du fichier par le service d'importation
            await _signalRService.ProcessCsvUpload(
                connectionId,
                file.OpenReadStream(),
                file.FileName,
                config.ConfigData
            );

            return Ok(new
            {
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
            return StatusCode(500,
                new { error = "Erreur interne du serveur lors de l\'upload du fichier", details = ex.Message });
        }
    }

    [Authorize]
    [DisableRequestSizeLimit] 
    [RequestSizeLimit(100 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [HttpPost("upload-file-only")]
    public async Task<IActionResult> UploadFileOnly(
        IFormFile file,
        string connectionId)
    {
        try
        {
            if (file == null) return BadRequest(new { error = "Aucun fichier fourni" });

            _logger.LogInformation($"📁 [FileImportController] Upload du fichier {file.FileName} sans analyse automatique");

            // Valider le type de fichier
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allParsers = _serviceProvider.GetServices<ISpreadsheetDataParser>();
            var parserService = allParsers.FirstOrDefault(p => p.CanHandle(extension));
            
            if (parserService == null)
            {
                _logger.LogError($"❌ [FileImportController] Aucun parser trouvé pour l'extension {extension}");
                return BadRequest(new { error = $"Type de fichier non supporté: {extension}" });
            }

            // 🔧 CORRECTION: Stocker le fichier brut au lieu de le parser immédiatement
            // Le parsing se fera lors de l'analyse avec les bonnes manualColumns
            await using var stream = file.OpenReadStream();
            var fileBytes = new byte[stream.Length];
            await stream.ReadAsync(fileBytes, 0, (int)stream.Length);
            
            // Stocker les données brutes du fichier avec les métadonnées
            var fileData = new Dictionary<string, object>
            {
                ["fileName"] = file.FileName,
                ["fileBytes"] = fileBytes,
                ["fileSize"] = file.Length,
                ["extension"] = extension,
                ["uploadTime"] = DateTime.Now
            };
            
            // Utiliser un store temporaire pour les fichiers bruts
            FileDataStore.SetRawFileData(fileData, connectionId);

            _logger.LogInformation($"✅ [FileImportController] Fichier brut {file.FileName} stocké ({file.Length} bytes) pour connexion {connectionId}");

            return Ok(new
            {
                message = "Fichier uploadé avec succès (sera parsé lors de l'analyse)",
                fileName = file.FileName,
                fileSize = file.Length,
                timestamp = DateTime.Now,
                extension = extension,
                analysisInitiated = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [FileImportController] Erreur lors de l\'upload du fichier");
            _logService.Log("IMPORT", $"Erreur lors de l\'upload du fichier: {ex.Message}");
            return StatusCode(500,
                new { error = "Erreur interne du serveur lors de l\'upload du fichier", details = ex.Message });
        }
    }

    [HttpGet("system/check-websocket")]
    [Authorize]
    public async Task<IActionResult> CheckWebSocketAvailability()
    {
        _logService.Log("SYSTEM", "Vérification de la disponibilité de WebSocket via SignalR.");
        var isConnected = await _signalRService.IsConnectedAsync();
        if (isConnected) return Ok(new { status = "Connecté", message = "SignalR (WebSocket) semble fonctionner." });

        return StatusCode(503,
            new { status = "Non connecté", message = "SignalR (WebSocket) ne semble pas connecté." });
    }

    [HttpGet("system/check-signalr")]
    [Authorize]
    public async Task<IActionResult> CheckSignalRAvailability()
    {
        _logService.Log("SYSTEM", "Vérification de la disponibilité de SignalR.");
        var isConnected = await _signalRService.IsConnectedAsync();
        if (isConnected) return Ok(new { status = "Connecté", message = "SignalR semble fonctionner." });

        return StatusCode(503, new { status = "Non connecté", message = "SignalR ne semble pas connecté." });
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
            return (false, "Aucun fichier n'a été sélectionné ou le fichier est vide.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension != ".csv" && extension != ".xlsx")
            return (false, "Seuls les fichiers CSV ou XLSX sont autorisés.");
        return (true, string.Empty);
    }

    private async Task<(bool IsValid, string ErrorMessage, SavedImportConfig Config)> GetAndValidateConfig(
        string configId)
    {
        if (string.IsNullOrEmpty(configId)) return (false, "L'ID de configuration est manquant.", null);

        var configs = await _configService.GetSavedImportConfigs();
        var config = configs.FirstOrDefault(c => c.Id == configId);

        if (config == null) return (false, $"Configuration avec l'ID '{configId}' non trouvée.", null);
        return (true, string.Empty, config);
    }

    // --- Méthodes pour les templates prédéfinis ---
    private ImportConfigTemplate? GetTemplateById(string templateId)
    {
        return templateId switch
        {
            "lycee-students-basic" => GetLyceeStudentsBasicTemplate(),
            "lycee-students-teams" => GetLyceeStudentsTeamsTemplate(),
            "enterprise-users" => GetEnterpriseUsersTemplate(),
            _ => null
        };
    }

    private ImportConfigTemplate GetLyceeStudentsBasicTemplate()
    {
        return new ImportConfigTemplate
        {
            Id = "lycee-students-basic",
            Name = "Lycée - Élèves (Base)",
            Description = "Configuration de base pour l'import d'élèves",
            Category = "Éducation",
            IsSystemTemplate = true,
            ConfigData = new ImportConfig
            {
                DefaultOU = "OU=CLASSES,DC=lycee,DC=nd",
                CsvDelimiter = ';',
                CreateMissingOUs = true,
                OverwriteExisting = true,
                MoveObjects = true,
                HeaderMapping = new Dictionary<string, string>
                {
                    { "sAMAccountName", "%prenom:username%.%nom:username%" },
                    { "userPrincipalName", "%prenom:username%.%nom:username%@lycee-ndchallans.com" },
                    { "mail", "%prenom:username%.%nom:username%@lycee-ndchallans.com" },
                    { "givenName", "%prenom%" },
                    { "sn", "%nom:uppercase%" },
                    { "cn", "%prenom% %nom%" },
                    { "displayName", "%prenom% %nom:uppercase%" },
                    { "division", "%classe%" },
                    { "company", "Lycée Notre-Dame" }
                },
                ManualColumns = new List<string> { "prenom", "nom", "classe", "dateNaissance" },
                ouColumn = "classe",
                Folders = new FolderConfig
                {
                    EnableShareProvisioning = true,
                    HomeDirectoryTemplate = "\\\\192.168.10.43\\Data\\%username%",
                    HomeDriveLetter = "D:",
                    TargetServerName = "192.168.10.43",
                    DefaultShareSubfolders = new List<string> { "Documents", "Desktop" }
                }
            }
        };
    }

    private ImportConfigTemplate GetLyceeStudentsTeamsTemplate()
    {
        var config = GetLyceeStudentsBasicTemplate();
        config.Id = "lycee-students-teams";
        config.Name = "Lycée - Élèves avec Teams";
        config.Description = "Configuration avec intégration Microsoft Teams";

        config.ConfigData.TeamsIntegration = new TeamsImportConfig
        {
            Enabled = true,
            AutoAddUsersToTeams = true,
            TeamNamingTemplate = "Classe {OUName} - Année 2025",
            TeamDescriptionTemplate = "Équipe collaborative pour la classe {OUName}",
            FolderMappings = new List<TeamsFolderMapping>
            {
                new()
                {
                    FolderName = "📚 Documents de classe",
                    Description = "Dossier pour tous les documents partagés de la classe",
                    Order = 1,
                    Enabled = true,
                    DefaultPermissions = new TeamsFolderPermissions
                    {
                        CanRead = true,
                        CanWrite = true,
                        CanDelete = false,
                        CanCreateSubfolders = true
                    }
                },
                new()
                {
                    FolderName = "📝 Devoirs et Exercices",
                    Description = "Dossier pour les devoirs à rendre et exercices",
                    ParentFolder = "📚 Documents de classe",
                    Order = 2,
                    Enabled = true,
                    DefaultPermissions = new TeamsFolderPermissions
                    {
                        CanRead = true,
                        CanWrite = false,
                        CanDelete = false,
                        CanCreateSubfolders = false
                    }
                },
                new()
                {
                    FolderName = "🔬 Projets de Groupe",
                    Description = "Espace collaboratif pour les projets d'équipe",
                    Order = 3,
                    Enabled = true,
                    DefaultPermissions = new TeamsFolderPermissions
                    {
                        CanRead = true,
                        CanWrite = true,
                        CanDelete = true,
                        CanCreateSubfolders = true
                    }
                }
            }
        };

        return config;
    }

    private ImportConfigTemplate GetEnterpriseUsersTemplate()
    {
        return new ImportConfigTemplate
        {
            Id = "enterprise-users",
            Name = "Entreprise - Utilisateurs",
            Description = "Configuration pour import d'utilisateurs d'entreprise",
            Category = "Entreprise",
            IsSystemTemplate = true,
            ConfigData = new ImportConfig
            {
                DefaultOU = "OU=Users,DC=company,DC=com",
                CsvDelimiter = ',',
                CreateMissingOUs = true,
                OverwriteExisting = true,
                HeaderMapping = new Dictionary<string, string>
                {
                    { "sAMAccountName", "%username%" },
                    { "userPrincipalName", "%username%@company.com" },
                    { "mail", "%email%" },
                    { "givenName", "%firstname%" },
                    { "sn", "%lastname%" },
                    { "title", "%jobtitle%" },
                    { "department", "%department%" }
                },
                ManualColumns = new List<string> { "username", "firstname", "lastname", "email", "department" },
                Folders = new FolderConfig
                {
                    EnableShareProvisioning = false
                }
            }
        };
    }
}