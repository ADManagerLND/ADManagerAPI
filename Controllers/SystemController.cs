using Microsoft.AspNetCore.Mvc;
using ADManagerAPI.Services.Interfaces;

namespace ADManagerAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SystemController : ControllerBase
    {
        private readonly ISignalRService _signalRService;
        private readonly ILogger<SystemController> _logger;

        public SystemController(
            ISignalRService signalRService,
            ILogger<SystemController> logger)
        {
            _signalRService = signalRService;
            _logger = logger;
        }

        [HttpGet("check-signalr")]
        public async Task<IActionResult> CheckSignalR()
        {
            try
            {
                _logger.LogInformation("Vérification de la disponibilité de SignalR");
                
                // Vérifier si SignalR est disponible
                var isAvailable = await _signalRService.IsConnectedAsync();
                
                return Ok(new { 
                    available = isAvailable,
                    message = isAvailable 
                        ? "SignalR est disponible" 
                        : "SignalR n'est pas disponible actuellement"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la vérification de la disponibilité de SignalR");
                return Ok(new { 
                    available = false,
                    message = "Erreur lors de la vérification de SignalR"
                });
            }
        }
        
        [HttpGet("info")]
        public IActionResult GetSystemInfo()
        {
            var info = new
            {
                version = "1.0.0",
                supportedConnectionTypes = new[] { "signalr" },
                defaultConnectionType = "signalr"
            };
            
            return Ok(info);
        }
    }
} 