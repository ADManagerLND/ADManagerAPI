using Microsoft.AspNetCore.Mvc;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using System.ComponentModel.DataAnnotations;

namespace ADManagerAPI.Controllers.Teams
{
    /// <summary>
    /// Contrôleur pour la gestion de l'intégration Teams avec Active Directory
    /// OPTIONNEL - Fonctionne même si l'intégration Teams est désactivée
    /// </summary>
    [ApiController]
    [Route("api/teams-integration")]
    [Produces("application/json")]
    public class TeamsIntegrationController : ControllerBase
    {
        private readonly ITeamsIntegrationService _teamsIntegrationService;
        private readonly IOUTeamsMapperService? _mapperService;
        private readonly ILdapService _ldapService;
        private readonly ILogger<TeamsIntegrationController> _logger;

        public TeamsIntegrationController(
            ITeamsIntegrationService teamsIntegrationService,
            ILdapService ldapService,
            ILogger<TeamsIntegrationController> logger,
            IOUTeamsMapperService? mapperService = null)
        {
            _teamsIntegrationService = teamsIntegrationService;
            _ldapService = ldapService;
            _logger = logger;
            _mapperService = mapperService;
        }

        /// <summary>
        /// Récupère le statut de santé de l'intégration Teams
        /// </summary>
        /// <returns>Statut de santé détaillé</returns>
        [HttpGet("health")]
        [ProducesResponseType(typeof(TeamsIntegrationHealthStatus), StatusCodes.Status200OK)]
        public async Task<ActionResult<TeamsIntegrationHealthStatus>> GetHealthStatusAsync()
        {
            try
            {
                var healthStatus = await _teamsIntegrationService.GetHealthStatusAsync();
                
                // Retourner le bon code de statut HTTP selon la santé
                if (healthStatus.IsHealthy || !healthStatus.Enabled)
                {
                    return Ok(healthStatus);
                }
                else
                {
                    return StatusCode(503, healthStatus); // Service Unavailable
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors du health check Teams");
                return StatusCode(500, new TeamsIntegrationHealthStatus
                {
                    IsHealthy = false,
                    Status = "Error",
                    Issues = { $"Health check failed: {ex.Message}" }
                });
            }
        }

        /// <summary>
        /// Crée manuellement une équipe Teams pour une OU spécifique
        /// </summary>
        /// <param name="request">Paramètres de création de l'équipe</param>
        /// <returns>Résultat de la création</returns>
        [HttpPost("create-team")]
        [ProducesResponseType(typeof(TeamsCreationResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<TeamsCreationResult>> CreateTeamAsync([FromBody] TeamCreationRequest request)
        {
            try
            {
                _logger.LogInformation("🎯 Demande création équipe Teams manuelle pour OU '{OUName}'", request.OUName);

                // Validation
                if (string.IsNullOrWhiteSpace(request.OUName) || string.IsNullOrWhiteSpace(request.OUPath))
                {
                    return BadRequest("OUName et OUPath sont requis");
                }

                // Vérifier que l'OU existe
                var ouExists = await _ldapService.OrganizationalUnitExistsAsync(request.OUPath);
                if (!ouExists)
                {
                    return BadRequest($"L'OU '{request.OUPath}' n'existe pas dans Active Directory");
                }

                // Créer l'équipe Teams
                var result = await _teamsIntegrationService.CreateTeamFromOUAsync(
                    request.OUName, 
                    request.OUPath, 
                    request.TeacherUserId);

                if (result.Success)
                {
                    _logger.LogInformation("✅ Équipe Teams créée manuellement: {TeamId}", result.TeamId);
                    return Ok(result);
                }
                else
                {
                    _logger.LogWarning("⚠️ Échec création équipe Teams: {Error}", result.ErrorMessage);
                    return BadRequest(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la création manuelle d'équipe Teams");
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }

        /// <summary>
        /// Synchronise les utilisateurs d'une OU vers son équipe Teams
        /// </summary>
        /// <param name="ouDn">Distinguished Name de l'OU</param>
        /// <returns>Résultat de la synchronisation</returns>
        [HttpPost("sync-users")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<bool>> SyncUsersAsync([FromQuery, Required] string ouDn)
        {
            try
            {
                _logger.LogInformation("🔄 Demande synchronisation utilisateurs pour OU '{OUDN}'", ouDn);

                if (string.IsNullOrWhiteSpace(ouDn))
                {
                    return BadRequest("Le paramètre ouDn est requis");
                }

                // Vérifier que l'OU a une équipe Teams associée si le service est activé
                if (_mapperService != null)
                {
                    var mapping = await _mapperService.GetMappingAsync(ouDn);
                    if (mapping == null)
                    {
                        return NotFound($"Aucune équipe Teams trouvée pour l'OU '{ouDn}'");
                    }
                }

                var result = await _teamsIntegrationService.SyncOUUsersToTeamAsync(ouDn);

                _logger.LogInformation("✅ Synchronisation terminée pour OU '{OUDN}': {Success}", ouDn, result);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la synchronisation utilisateurs OU '{OUDN}'", ouDn);
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }

        /// <summary>
        /// Migre toutes les OUs existantes vers Microsoft Teams
        /// </summary>
        /// <returns>Liste des résultats de migration</returns>
        [HttpPost("migrate-all-ous")]
        [ProducesResponseType(typeof(List<TeamsCreationResult>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<List<TeamsCreationResult>>> MigrateAllOUsAsync()
        {
            try
            {
                _logger.LogInformation("🚀 Demande migration complète des OUs vers Teams");

                var results = await _teamsIntegrationService.MigrateExistingOUsAsync();

                var successful = results.Count(r => r.Success);
                var failed = results.Count(r => !r.Success);

                _logger.LogInformation("✅ Migration terminée: {Successful} succès, {Failed} échecs", successful, failed);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la migration complète des OUs");
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }

        /// <summary>
        /// Récupère tous les mappings OU → Teams actifs (si disponible)
        /// </summary>
        /// <returns>Liste des mappings</returns>
        [HttpGet("mappings")]
        [ProducesResponseType(typeof(List<OUTeamsMapping>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<List<OUTeamsMapping>>> GetMappingsAsync()
        {
            try
            {
                if (_mapperService == null)
                {
                    return StatusCode(503, "Service de mapping Teams non disponible (intégration Teams désactivée)");
                }

                var mappings = await _mapperService.GetAllMappingsAsync();
                _logger.LogDebug("📊 {Count} mappings actifs retournés", mappings.Count);
                return Ok(mappings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur récupération mappings");
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }

        /// <summary>
        /// Récupère le mapping Teams pour une OU spécifique
        /// </summary>
        /// <param name="ouDn">Distinguished Name de l'OU</param>
        /// <returns>Mapping OU → Teams</returns>
        [HttpGet("mappings/{ouDn}")]
        [ProducesResponseType(typeof(OUTeamsMapping), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<OUTeamsMapping>> GetMappingAsync([FromRoute] string ouDn)
        {
            try
            {
                if (_mapperService == null)
                {
                    return StatusCode(503, "Service de mapping Teams non disponible (intégration Teams désactivée)");
                }

                if (string.IsNullOrWhiteSpace(ouDn))
                {
                    return BadRequest("Le paramètre ouDn est requis");
                }

                // Décoder l'URL
                ouDn = Uri.UnescapeDataString(ouDn);

                var mapping = await _mapperService.GetMappingAsync(ouDn);
                if (mapping == null)
                {
                    return NotFound($"Aucun mapping trouvé pour l'OU '{ouDn}'");
                }

                return Ok(mapping);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur récupération mapping pour OU '{OUDN}'", ouDn);
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }

        /// <summary>
        /// Supprime un mapping OU → Teams
        /// </summary>
        /// <param name="ouDn">Distinguished Name de l'OU</param>
        /// <returns>Confirmation de suppression</returns>
        [HttpDelete("mappings/{ouDn}")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<bool>> RemoveMappingAsync([FromRoute] string ouDn)
        {
            try
            {
                if (_mapperService == null)
                {
                    return StatusCode(503, "Service de mapping Teams non disponible (intégration Teams désactivée)");
                }

                if (string.IsNullOrWhiteSpace(ouDn))
                {
                    return BadRequest("Le paramètre ouDn est requis");
                }

                // Décoder l'URL
                ouDn = Uri.UnescapeDataString(ouDn);

                var existingMapping = await _mapperService.GetMappingAsync(ouDn);
                if (existingMapping == null)
                {
                    return NotFound($"Aucun mapping trouvé pour l'OU '{ouDn}'");
                }

                await _mapperService.RemoveMappingAsync(ouDn);
                _logger.LogInformation("🗑️ Mapping supprimé pour OU '{OUDN}'", ouDn);

                return Ok(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur suppression mapping pour OU '{OUDN}'", ouDn);
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }

        /// <summary>
        /// Resynchronise manuellement une OU spécifique
        /// </summary>
        /// <param name="ouDn">Distinguished Name de l'OU</param>
        /// <returns>Résultat de la resynchronisation</returns>
        [HttpPost("resync")]
        [ProducesResponseType(typeof(TeamsCreationResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<TeamsCreationResult>> ResyncOUAsync([FromQuery, Required] string ouDn)
        {
            try
            {
                _logger.LogInformation("🔄 Demande resynchronisation OU '{OUDN}'", ouDn);

                if (string.IsNullOrWhiteSpace(ouDn))
                {
                    return BadRequest("Le paramètre ouDn est requis");
                }

                var result = await _teamsIntegrationService.ResyncOUToTeamAsync(ouDn);
                
                _logger.LogInformation("✅ Resynchronisation terminée pour OU '{OUDN}': {Success}", ouDn, result.Success);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la resynchronisation OU '{OUDN}'", ouDn);
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }

        /// <summary>
        /// Ajoute manuellement un utilisateur à l'équipe Teams de son OU
        /// </summary>
        /// <param name="request">Détails de l'utilisateur à ajouter</param>
        /// <returns>Résultat de l'ajout</returns>
        [HttpPost("add-user-to-team")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<bool>> AddUserToTeamAsync([FromBody] AddUserToTeamRequest request)
        {
            try
            {
                _logger.LogInformation("👤 Demande ajout utilisateur '{User}' à équipe Teams (OU: {OUDN})", 
                    request.SamAccountName, request.OUDistinguishedName);

                if (string.IsNullOrWhiteSpace(request.SamAccountName) || string.IsNullOrWhiteSpace(request.OUDistinguishedName))
                {
                    return BadRequest("SamAccountName et OUDistinguishedName sont requis");
                }

                var result = await _teamsIntegrationService.AddUserToOUTeamAsync(request.SamAccountName, request.OUDistinguishedName);
                
                _logger.LogInformation("✅ Ajout utilisateur terminé: {Success}", result);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur ajout utilisateur '{User}' à Teams", request?.SamAccountName);
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }

        /// <summary>
        /// Récupère les statistiques de l'intégration Teams
        /// </summary>
        /// <returns>Statistiques détaillées</returns>
        [HttpGet("stats")]
        [ProducesResponseType(typeof(TeamsIntegrationStats), StatusCodes.Status200OK)]
        public async Task<ActionResult<TeamsIntegrationStats>> GetStatsAsync()
        {
            try
            {
                var allOUs = await _ldapService.GetAllOrganizationalUnitsAsync();
                
                var stats = new TeamsIntegrationStats
                {
                    TotalOUs = allOUs.Count,
                    LastSync = DateTime.UtcNow
                };

                if (_mapperService != null)
                {
                    var mappings = await _mapperService.GetAllMappingsAsync();
                    stats.OUsWithTeams = mappings.Count;
                    stats.TeamsCreated = mappings.Count;
                    stats.LastSync = mappings.Any() ? mappings.Max(m => m.LastSyncAt) : DateTime.MinValue;
                    stats.SuccessRate = allOUs.Count > 0 ? (double)mappings.Count / allOUs.Count * 100 : 0;
                    stats.OperationCounts = new Dictionary<string, int>
                    {
                        ["MappingsActifs"] = mappings.Count(m => m.IsActive),
                        ["MembresTotal"] = mappings.Sum(m => m.MemberCount)
                    };
                }
                else
                {
                    stats.OperationCounts["ServiceStatus"] = 0; // Teams désactivé
                }

                _logger.LogDebug("📈 Statistiques Teams générées: {TotalOUs} OUs, {TeamsCreated} équipes", 
                    stats.TotalOUs, stats.TeamsCreated);

                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur génération statistiques Teams");
                return StatusCode(500, $"Erreur interne: {ex.Message}");
            }
        }
    }
}