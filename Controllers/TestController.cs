using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ADManagerAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class TestController : ControllerBase
    {
        private readonly ILogger<TestController> _logger;

        public TestController(ILogger<TestController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public IActionResult TestApi()
        {
            try
            {
                // Récupérer les informations de l'utilisateur authentifié
                var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var userName = User.FindFirst(ClaimTypes.Name)?.Value;
                var userEmail = User.FindFirst(ClaimTypes.Email)?.Value;
                var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

                // Vérifier les claims de l'utilisateur
                var allClaims = User.Claims.Select(c => new { c.Type, c.Value }).ToList();

                // Obtenir des informations sur la connexion
                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                var requestMethod = HttpContext.Request.Method;
                var requestPath = HttpContext.Request.Path;
                var timestamp = DateTime.UtcNow;

                // Créer la réponse
                var response = new
                {
                    message = "API Test réussi!",
                    timestamp,
                    connection = new
                    {
                        ipAddress,
                        requestMethod,
                        requestPath
                    },
                    auth = new
                    {
                        userId,
                        userName,
                        userEmail,
                        roles,
                        claims = allClaims
                    }
                };

                _logger.LogInformation("Test d'API réussi pour l'utilisateur {UserName}", userName);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du test d'API");
                return StatusCode(500, new { message = "Erreur interne du serveur", error = ex.Message });
            }
        }

        [HttpGet("public")]
        [AllowAnonymous]
        public IActionResult PublicTest()
        {
            _logger.LogInformation("Test d'API publique réussi");
            return Ok(new
            {
                message = "API publique fonctionnelle",
                timestamp = DateTime.UtcNow,
                serverInfo = new
                {
                    machineName = Environment.MachineName,
                    osVersion = Environment.OSVersion.ToString(),
                    processorCount = Environment.ProcessorCount,
                    isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                }
            });
        }
    }
} 