using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ILogger<AuthController> _logger;

    public AuthController(ILogger<AuthController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Point de terminaison pour obtenir les informations de l'utilisateur Azure AD authentifié
    /// </summary>
    [HttpGet("user")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult GetUser()
    {
        try
        {
            // Récupérer les claims de l'utilisateur
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                             User.FindFirst("oid")?.Value ??
                             User.FindFirst("sub")?.Value;

            var userNameClaim = User.FindFirst(ClaimTypes.Name)?.Value ??
                               User.FindFirst("preferred_username")?.Value ??
                               User.FindFirst("upn")?.Value ??
                               User.FindFirst("unique_name")?.Value;

            var emailClaim = User.FindFirst(ClaimTypes.Email)?.Value ??
                            User.FindFirst("email")?.Value ??
                            User.FindFirst("preferred_username")?.Value;

            var displayNameClaim = User.FindFirst("name")?.Value ??
                                  User.FindFirst(ClaimTypes.GivenName)?.Value ??
                                  userNameClaim;

            var tenantIdClaim = User.FindFirst("tid")?.Value;

            // Récupérer tous les claims pour le débogage
            var allClaims = User.Claims.Select(c => new { 
                Type = c.Type, 
                Value = c.Value 
            }).ToList();

            _logger.LogInformation("Utilisateur authentifié: {Username} (ID: {UserId})", 
                                 userNameClaim, userIdClaim);

            return Ok(new
            {
                UserId = userIdClaim,
                Username = userNameClaim,
                Email = emailClaim,
                DisplayName = displayNameClaim,
                TenantId = tenantIdClaim,
                IsAuthenticated = User.Identity?.IsAuthenticated ?? false,
                AuthenticationType = User.Identity?.AuthenticationType,
                Claims = allClaims, // Pour le débogage
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des informations utilisateur");
            return StatusCode(500, new 
            { 
                Message = "Erreur lors de la récupération des informations utilisateur",
                Error = ex.Message 
            });
        }
    }

    /// <summary>
    /// Point de terminaison pour vérifier l'état d'authentification
    /// </summary>
    [HttpGet("status")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult GetAuthStatus()
    {
        return Ok(new
        {
            IsAuthenticated = User.Identity?.IsAuthenticated ?? false,
            AuthenticationType = User.Identity?.AuthenticationType,
            Username = User.Identity?.Name,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Point de terminaison public pour tester la connectivité (sans authentification)
    /// </summary>
    [HttpGet("ping")]
    [AllowAnonymous]
    public IActionResult Ping()
    {
        return Ok(new
        {
            Message = "API d'authentification accessible",
            Timestamp = DateTime.UtcNow,
            Server = Environment.MachineName
        });
    }

    /// <summary>
    /// Point de terminaison pour obtenir les informations de configuration Azure AD (sans données sensibles)
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public IActionResult GetAuthConfig([FromServices] IConfiguration configuration)
    {
        var azureAdConfig = configuration.GetSection("AzureAd");
        
        return Ok(new
        {
            Authority = $"https://login.microsoftonline.com/{azureAdConfig["TenantId"]}/v2.0",
            ClientId = azureAdConfig["ClientId"],
            TenantId = azureAdConfig["TenantId"],
            Audience = azureAdConfig["Audience"],
            Timestamp = DateTime.UtcNow
        });
    }
}