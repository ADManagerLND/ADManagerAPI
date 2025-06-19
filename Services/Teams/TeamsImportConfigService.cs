using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;

namespace ADManagerAPI.Services.Teams;

/// <summary>
///     Service pour gérer les configurations Teams spécifiques aux imports
/// </summary>
public class TeamsImportConfigService
{
    private readonly IConfigService _configService;
    private readonly ILogger<TeamsIntegrationService> _logger;

    public TeamsImportConfigService(
        ILogger<TeamsIntegrationService> logger,
        IConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    /// <summary>
    ///     Récupère la configuration Teams pour un import spécifique
    /// </summary>
    public async Task<TeamsImportConfig?> GetTeamsConfigForImportAsync(string importId)
    {
        try
        {
            var importConfigs = await _configService.GetSavedImportConfigs();
            var importConfig = importConfigs.FirstOrDefault(c => c.Id == importId);

            return importConfig?.ConfigData?.TeamsIntegration;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration Teams pour l'import {ImportId}",
                importId);
            return null;
        }
    }

    /// <summary>
    ///     Met à jour la configuration Teams pour un import spécifique
    /// </summary>
    public async Task<bool> UpdateTeamsConfigForImportAsync(string importId, TeamsImportConfig teamsConfig)
    {
        try
        {
            var importConfigs = await _configService.GetSavedImportConfigs();
            var importConfig = importConfigs.FirstOrDefault(c => c.Id == importId);

            if (importConfig == null)
            {
                _logger.LogWarning("Configuration d'import non trouvée: {ImportId}", importId);
                return false;
            }

            importConfig.ConfigData.TeamsIntegration = teamsConfig;
            await _configService.SaveImportConfig(importConfig);

            _logger.LogInformation("Configuration Teams mise à jour pour l'import {ImportId}", importId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration Teams pour l'import {ImportId}",
                importId);
            return false;
        }
    }

    /// <summary>
    ///     Crée une configuration Teams par défaut pour un import
    /// </summary>
    public TeamsImportConfig CreateDefaultTeamsConfig()
    {
        return new TeamsImportConfig
        {
            Enabled = false,
            AutoAddUsersToTeams = true,
            TeamNamingTemplate = "Classe {OUName}",
            TeamDescriptionTemplate = "Équipe pour la classe {OUName}",
            FolderMappings = GetDefaultFolderMappings()
        };
    }

    /// <summary>
    ///     Récupère les dossiers par défaut à créer dans Teams
    /// </summary>
    private List<TeamsFolderMapping> GetDefaultFolderMappings()
    {
        return new List<TeamsFolderMapping>
        {
            new()
            {
                FolderName = "Documents de classe",
                Description = "Dossier pour les documents partagés de la classe",
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
                FolderName = "Devoirs",
                Description = "Dossier pour les devoirs et exercices",
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
                FolderName = "Projets",
                Description = "Dossier pour les projets de groupe",
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
        };
    }

    /// <summary>
    ///     Valide une configuration Teams
    /// </summary>
    public async Task<(bool IsValid, List<string> Errors)> ValidateTeamsConfigAsync(TeamsImportConfig config)
    {
        var errors = new List<string>();

        if (config.Enabled)
        {
            // Vérifier la configuration Azure AD
            var azureConfig = await _configService.GetAzureADConfigAsync();
            if (string.IsNullOrEmpty(azureConfig.ClientId))
                errors.Add("Client ID Azure AD manquant");
            if (string.IsNullOrEmpty(azureConfig.TenantId))
                errors.Add("Tenant ID Azure AD manquant");
            if (string.IsNullOrEmpty(azureConfig.ClientSecret))
                errors.Add("Client Secret Azure AD manquant");

            // Vérifier les templates
            if (string.IsNullOrEmpty(config.TeamNamingTemplate))
                errors.Add("Template de nom d'équipe manquant");
            if (string.IsNullOrEmpty(config.TeamDescriptionTemplate))
                errors.Add("Template de description d'équipe manquant");

            // Vérifier les dossiers
            if (config.FolderMappings.Any(f => string.IsNullOrEmpty(f.FolderName)))
                errors.Add("Certains dossiers n'ont pas de nom");
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>
    ///     Applique la configuration Teams depuis un import lors de la création d'équipes
    /// </summary>
    public async Task<TeamsCreationResult> CreateTeamFromImportConfigAsync(
        string importId,
        string ouName,
        string ouPath,
        string? teacherId = null)
    {
        var result = new TeamsCreationResult
        {
            ClassName = ouName,
            OUPath = ouPath
        };

        try
        {
            var teamsConfig = await GetTeamsConfigForImportAsync(importId);
            if (teamsConfig == null || !teamsConfig.Enabled)
            {
                result.ErrorMessage = "Configuration Teams non trouvée ou désactivée pour cet import";
                return result;
            }

            // Valider la configuration
            var (isValid, errors) = await ValidateTeamsConfigAsync(teamsConfig);
            if (!isValid)
            {
                result.ErrorMessage = $"Configuration Teams invalide: {string.Join(", ", errors)}";
                return result;
            }

            // Générer le nom et la description de l'équipe
            var teamName = teamsConfig.TeamNamingTemplate.Replace("{OUName}", ouName);
            var teamDescription = teamsConfig.TeamDescriptionTemplate.Replace("{OUName}", ouName);

            // Résoudre l'enseignant
            var resolvedTeacherId = ResolveTeacherId(teamsConfig, ouPath, teacherId);
            if (string.IsNullOrEmpty(resolvedTeacherId))
            {
                result.ErrorMessage = "Aucun enseignant disponible pour créer l'équipe";
                return result;
            }

            result.AdditionalInfo["TeamName"] = teamName;
            result.AdditionalInfo["TeamDescription"] = teamDescription;
            result.AdditionalInfo["TeacherId"] = resolvedTeacherId;
            result.AdditionalInfo["FoldersToCreate"] =
                teamsConfig.FolderMappings.Where(f => f.Enabled).Select(f => f.FolderName).ToList();

            // Note: La création effective de l'équipe doit être gérée par TeamsIntegrationService
            // Ici on prépare seulement les données

            _logger.LogInformation("Configuration Teams préparée pour l'import {ImportId}, OU {OUName}", importId,
                ouName);
            result.Success = true;

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Erreur lors de la préparation de la configuration Teams: {ex.Message}";
            _logger.LogError(ex, "Erreur lors de la création d'équipe depuis la configuration d'import {ImportId}",
                importId);
            return result;
        }
    }

    private string? ResolveTeacherId(TeamsImportConfig config, string ouPath, string? providedTeacherId)
    {
        // 1. Utiliser l'enseignant fourni explicitement
        if (!string.IsNullOrEmpty(providedTeacherId))
            return providedTeacherId;

        /*// 2. Rechercher dans les mappings OU → Enseignant de la configuration d'import
        var mapping = config.OUTeacherMappings.FirstOrDefault(kvp =>
            ouPath.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(mapping.Value))
            return mapping.Value;*/

        // 3. Utiliser l'enseignant par défaut de la configuration d'import
        return config.DefaultTeacherUserId;
    }
}