using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ADManagerAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AzureConfigController : ControllerBase
{
    private readonly IConfigService _configService;
    private readonly ILogger<AzureConfigController> _logger;

    public AzureConfigController(
        ILogger<AzureConfigController> logger,
        IConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    /// <summary>
    ///     Récupère la configuration Azure AD
    /// </summary>
    [HttpGet("azure-ad")]
    public async Task<ActionResult<AzureADConfig>> GetAzureADConfig()
    {
        try
        {
            var config = await _configService.GetAzureADConfigAsync();

            // Masquer le client secret dans la réponse
            var safeConfig = new AzureADConfig
            {
                ClientId = config.ClientId,
                TenantId = config.TenantId,
                ClientSecret = string.IsNullOrEmpty(config.ClientSecret) ? null : "***"
            };

            return Ok(safeConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration Azure AD");
            return StatusCode(500, "Erreur serveur lors de la récupération de la configuration");
        }
    }

    /// <summary>
    ///     Met à jour la configuration Azure AD
    /// </summary>
    [HttpPut("azure-ad")]
    public async Task<ActionResult> UpdateAzureADConfig([FromBody] AzureADConfig config)
    {
        try
        {
            if (config == null) return BadRequest("Configuration Azure AD requise");

            // Si le client secret est "***", ne pas le modifier
            if (config.ClientSecret == "***")
            {
                var existingConfig = await _configService.GetAzureADConfigAsync();
                config.ClientSecret = existingConfig.ClientSecret;
            }

            await _configService.UpdateAzureADConfigAsync(config);

            _logger.LogInformation("Configuration Azure AD mise à jour");
            return Ok(new { message = "Configuration Azure AD mise à jour avec succès" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration Azure AD");
            return StatusCode(500, "Erreur serveur lors de la mise à jour de la configuration");
        }
    }

    /// <summary>
    ///     Récupère la configuration Graph API
    /// </summary>
    [HttpGet("graph-api")]
    public async Task<ActionResult<GraphApiConfig>> GetGraphApiConfig()
    {
        try
        {
            var config = await _configService.GetGraphApiConfigAsync();
            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration Graph API");
            return StatusCode(500, "Erreur serveur lors de la récupération de la configuration");
        }
    }

    /// <summary>
    ///     Met à jour la configuration Graph API
    /// </summary>
    [HttpPut("graph-api")]
    public async Task<ActionResult> UpdateGraphApiConfig([FromBody] GraphApiConfig config)
    {
        try
        {
            if (config == null) return BadRequest("Configuration Graph API requise");

            await _configService.UpdateGraphApiConfigAsync(config);

            _logger.LogInformation("Configuration Graph API mise à jour");
            return Ok(new { message = "Configuration Graph API mise à jour avec succès" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration Graph API");
            return StatusCode(500, "Erreur serveur lors de la mise à jour de la configuration");
        }
    }

    /// <summary>
    ///     Teste la configuration Azure AD
    /// </summary>
    [HttpPost("azure-ad/test")]
    public async Task<ActionResult> TestAzureADConfig()
    {
        try
        {
            var azureConfig = await _configService.GetAzureADConfigAsync();
            var isValid = !string.IsNullOrEmpty(azureConfig.ClientId) &&
                          !string.IsNullOrEmpty(azureConfig.TenantId) &&
                          !string.IsNullOrEmpty(azureConfig.ClientSecret);

            if (isValid) return Ok(new { message = "Configuration Azure AD valide", isValid = true });

            return BadRequest(new { message = "Configuration Azure AD incomplète", isValid = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors du test de la configuration Azure AD");
            return StatusCode(500, "Erreur serveur lors du test de la configuration");
        }
    }
}