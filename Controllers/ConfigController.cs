using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;

namespace ADManagerAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ConfigController : ControllerBase
    {
        private readonly IConfigService _configService;
        private readonly ILogger<ConfigController> _logger;
        
        public ConfigController(IConfigService configService, ILogger<ConfigController> logger)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        #region Configuration d'import
        
        [HttpGet("import")]
        public async Task<ActionResult<List<SavedImportConfig>>> GetSavedImportConfigs()
        {
            try
            {
                var configs = await _configService.GetSavedImportConfigs();
                return Ok(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération des configurations d'import");
                return StatusCode(500, "Une erreur est survenue lors de la récupération des configurations d'import");
            }
        }
        
        [HttpPost("import")]
        public async Task<ActionResult<SavedImportConfig>> SaveImportConfig([FromBody] SavedImportConfig config)
        {
            try
            {
                var savedConfig = await _configService.SaveImportConfig(config);
                return Ok(savedConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la sauvegarde de la configuration d'import");
                return StatusCode(500, "Une erreur est survenue lors de la sauvegarde de la configuration d'import");
            }
        }
        
        [HttpDelete("import/{configId}")]
        public async Task<ActionResult> DeleteImportConfig(string configId)
        {
            try
            {
                var result = await _configService.DeleteImportConfig(configId);
                if (!result)
                {
                    return NotFound($"Configuration d'import avec l'ID {configId} non trouvée");
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la suppression de la configuration d'import");
                return StatusCode(500, "Une erreur est survenue lors de la suppression de la configuration d'import");
            }
        }
        
        #endregion
        
        #region Configuration générale
        
        [HttpGet]
        public async Task<ActionResult<ApiSettings>> GetAppConfig()
        {
            try
            {
                var config = await _configService.GetApiSettingsAsync();
                return Ok(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération de la configuration");
                return StatusCode(500, "Une erreur est survenue lors de la récupération de la configuration");
            }
        }
        
        [HttpPut]
        public async Task<ActionResult> UpdateAppConfig([FromBody] ApiSettings config)
        {
            try
            {
                await _configService.UpdateApiSettingsAsync(config);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration");
                return StatusCode(500, "Une erreur est survenue lors de la mise à jour de la configuration");
            }
        }
        
        #endregion
        
        #region Configuration LDAP
        
        [HttpGet("ldap")]
        public async Task<ActionResult<LdapSettings>> GetLdapConfig()
        {
            try
            {
                var config = await _configService.GetLdapSettingsAsync();
                return Ok(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération de la configuration LDAP");
                return StatusCode(500, "Une erreur est survenue lors de la récupération de la configuration LDAP");
            }
        }
        
        [HttpPut("ldap")]
        public async Task<ActionResult> UpdateLdapConfig([FromBody] LdapSettings config)
        {
            try
            {
                await _configService.UpdateLdapSettingsAsync(config);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration LDAP");
                return StatusCode(500, "Une erreur est survenue lors de la mise à jour de la configuration LDAP");
            }
        }
        
        
        #endregion
        
        #region Attributs utilisateur
        
        [HttpGet("attributes")]
        public async Task<ActionResult<List<AdAttributeDefinition>>> GetUserAttributes()
        {
            try
            {
                var attributes = await _configService.GetUserAttributesAsync();
                return Ok(attributes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération des attributs utilisateur");
                return StatusCode(500, "Une erreur est survenue lors de la récupération des attributs utilisateur");
            }
        }
        
        [HttpPut("attributes")]
        public async Task<ActionResult> UpdateUserAttributes([FromBody] List<AdAttributeDefinition> attributes)
        {
            try
            {
                await _configService.UpdateUserAttributesAsync(attributes);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la mise à jour des attributs utilisateur");
                return StatusCode(500, "Une erreur est survenue lors de la mise à jour des attributs utilisateur");
            }
        }
        
        #endregion
        
        #region Configuration complète
        
        [HttpGet("all")]
        public async Task<ActionResult<ApplicationSettings>> GetAllSettings()
        {
            try
            {
                var settings = await _configService.GetAllSettingsAsync();
                return Ok(settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération de toute la configuration");
                return StatusCode(500, "Une erreur est survenue lors de la récupération de toute la configuration");
            }
        }
        
        [HttpPut("all")]
        public async Task<ActionResult> UpdateAllSettings([FromBody] ApplicationSettings settings)
        {
            try
            {
                await _configService.UpdateAllSettingsAsync(settings);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la mise à jour de toute la configuration");
                return StatusCode(500, "Une erreur est survenue lors de la mise à jour de toute la configuration");
            }
        }
        
        #endregion
    }
} 