using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ADManagerAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
public class SystemController : ControllerBase
{
    private readonly ILogger<SystemController> _logger;
    private readonly ISignalRService _signalRService;
    private readonly ILdapService _ldapService;
    private readonly IConfigService _configService;

    public SystemController(
        ISignalRService signalRService,
        ILdapService ldapService,
        IConfigService configService,
        ILogger<SystemController> logger)
    {
        _signalRService = signalRService;
        _ldapService = ldapService;
        _configService = configService;
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

            return Ok(new
            {
                available = isAvailable,
                message = isAvailable
                    ? "SignalR est disponible"
                    : "SignalR n'est pas disponible actuellement"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la vérification de la disponibilité de SignalR");
            return Ok(new
            {
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

    [HttpGet("dashboard-stats")]
    public async Task<IActionResult> GetDashboardStats()
    {
        try
        {
            _logger.LogInformation("📊 Récupération des statistiques du tableau de bord");

            // Récupérer la configuration pour obtenir le defaultOU
            var importConfigs = await _configService.GetSavedImportConfigs();
            var defaultOU = importConfigs.FirstOrDefault()?.ConfigData?.DefaultOU ?? "DC=lycee,DC=nd";

            // Compter les utilisateurs dans le defaultOU et ses sous-OUs
            var totalUsers = 0;
            var totalOUs = 0;
            var teamsCreated = 0;

            try
            {
                // Récupérer toutes les OUs
                var allOUs = await _ldapService.GetAllOrganizationalUnitsAsync();
                totalOUs = allOUs.Count;

                // Compter les utilisateurs dans le defaultOU et ses sous-OUs
                var defaultOUUsers = new List<string>();
                foreach (var ou in allOUs.Where(o => o.DistinguishedName.Contains(defaultOU, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var usersInOU = await _ldapService.GetUsersInOUAsync(ou.DistinguishedName);
                        defaultOUUsers.AddRange(usersInOU);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ Erreur lors du comptage des utilisateurs dans l'OU {OU}", ou.DistinguishedName);
                    }
                }
                totalUsers = defaultOUUsers.Distinct().Count();

                // Calculer les statistiques Teams si disponible
                try
                {
                    var teamsStatsResponse = await GetTeamsStats();
                    if (teamsStatsResponse is OkObjectResult okResult && okResult.Value != null)
                    {
                        var teamsStats = okResult.Value;
                        var teamsCreatedProperty = teamsStats.GetType().GetProperty("TeamsCreated");
                        if (teamsCreatedProperty != null)
                        {
                            teamsCreated = (int)(teamsCreatedProperty.GetValue(teamsStats) ?? 0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Erreur lors de la récupération des statistiques Teams");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Erreur lors de la récupération des données LDAP");
            }

            // Calculer le temps moyen (simulation basée sur les données historiques)
            var averageProcessingTime = totalUsers > 0 ? Math.Round((double)totalUsers / 300, 1) : 0; // ~300 comptes/minute

            var stats = new
            {
                importedAccounts = totalUsers,
                ouGroupsCount = totalOUs,
                averageProcessingTime = averageProcessingTime,
                teamsCreated = teamsCreated,
                lastSyncTime = DateTime.UtcNow.ToString("O"),
                successRate = totalUsers > 0 ? Math.Round(((double)(totalUsers - (totalUsers * 0.02)) / totalUsers) * 100, 1) : 100.0, // Simulation 98% de succès
                errorCount = totalUsers > 0 ? (int)(totalUsers * 0.02) : 0, // Simulation 2% d'erreurs
                defaultOU = defaultOU
            };

            _logger.LogInformation("✅ Statistiques générées: {Users} utilisateurs, {OUs} OUs, {Teams} équipes Teams", 
                totalUsers, totalOUs, teamsCreated);

            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la génération des statistiques du tableau de bord");
            return StatusCode(500, new { error = "Erreur interne lors de la récupération des statistiques" });
        }
    }

    private async Task<IActionResult> GetTeamsStats()
    {
        try
        {
            // Simulation d'appel aux statistiques Teams
            // Dans un vrai scénario, vous feriez appel au TeamsIntegrationController
            var allOUs = await _ldapService.GetAllOrganizationalUnitsAsync();
            
            var stats = new
            {
                TotalOUs = allOUs.Count,
                TeamsCreated = (int)(allOUs.Count * 0.3), // Simulation: 30% des OUs ont des équipes Teams
                LastSync = DateTime.UtcNow,
                SuccessRate = 95.0
            };

            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la récupération des statistiques Teams");
            return StatusCode(500, new { error = ex.Message });
        }
    }


}