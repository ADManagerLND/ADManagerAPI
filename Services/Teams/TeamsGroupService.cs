using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace ADManagerAPI.Services.Teams;

/// <summary>
///     Service Teams adapté pour implémenter ITeamsGroupService avec votre logique existante
///     Rendu OPTIONNEL - ne s'initialise que si la configuration Teams est activée
/// </summary>
public class TeamsGroupService : ITeamsGroupService
{
    private readonly IConfigService _configService;
    private readonly GraphServiceClient? _graphClient;
    private readonly bool _isInitialized;
    private readonly ILogger<TeamsGroupService> _logger;
    private bool _isEnabled;
    private TeamsIntegrationConfig _teamsIntegrationConfig;

    public TeamsGroupService(ILogger<TeamsGroupService> logger, IConfigService configService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));

        // Charger la configuration Teams au démarrage
        ReloadConfiguration();

        if (!_isEnabled)
        {
            _logger.LogInformation("🔕 Intégration Teams désactivée - Service en mode stub");
            _isInitialized = true; // 🆕 Marquer comme initialisé
            return;
        }

        try
        {
            var scopes = new[] { "https://graph.microsoft.com/.default" };

            // 🔧 AMÉLIORATION SÉCURITÉ : Récupération depuis la configuration Azure intégrée
            var azureConfig = _configService.GetAzureADConfigAsync().GetAwaiter().GetResult();

            var clientId = azureConfig.ClientId;
            var tenantId = azureConfig.TenantId;
            var clientSecret = azureConfig.ClientSecret;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientSecret))
            {
                _logger.LogWarning(
                    "⚠️ Configuration Azure AD incomplète dans TeamsIntegrationConfig - Intégration Teams désactivée");
                _isEnabled = false;
                _isInitialized = true;
                return;
            }

            var options = new ClientSecretCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
            };

            var clientSecretCredential = new ClientSecretCredential(tenantId, clientId, clientSecret, options);
            _graphClient = new GraphServiceClient(clientSecretCredential, scopes);

            _logger.LogInformation("✅ TeamsGroupService initialisé avec succès");
            _isInitialized = true; // 🆕 Marquer comme initialisé
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur initialisation TeamsGroupService - Mode désactivé");
            _isEnabled = false;
            _graphClient = null;
            _isInitialized = true; // 🆕 Marquer comme initialisé même en cas d'erreur
        }
    }

    /// <summary>
    ///     Crée une classe et équipe Teams complète (votre logique existante adaptée)
    /// </summary>
    public async Task<string> CreateClassTeamAsync(string className, string classDescription, string classMailNickname,
        string teacherUserId)
    {
        if (!EnsureEnabled())
            throw new InvalidOperationException("Service Teams non disponible");

        _logger.LogInformation("🚀 Création classe Teams: {ClassName} avec mailNickname '{MailNickname}'", className, classMailNickname);

        try
        {
            // 1. Créer la classe éducation
            var classId = await CreateEducationClassAsync(className, classDescription, classMailNickname);

            // 2. Ajouter l'enseignant
            await AddTeacherToClassAsync(classId, teacherUserId);

            // 3. Créer l'équipe Teams associée
            await CreateTeamForClassAsync(classId);

            _logger.LogInformation("✅ Classe et équipe créées avec succès: {ClassId} avec mailNickname '{MailNickname}'", 
                classId, classMailNickname);
            return classId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Échec création classe Teams {ClassName} avec mailNickname '{MailNickname}': {Error}",
                className, classMailNickname, ex.Message);
            throw;
        }
    }

    /// <summary>
    ///     Ajoute des membres à une équipe Teams
    /// </summary>
    public async Task<bool> AddMembersToTeamAsync(string groupId, List<string> userIds)
    {
        if (!EnsureEnabled())
            return false;

        try
        {
            _logger.LogInformation("👥 Ajout de {Count} membres à l'équipe {GroupId}", userIds.Count, groupId);

            foreach (var userId in userIds)
                try
                {
                    var memberRef = new ReferenceCreate
                    {
                        OdataId = $"https://graph.microsoft.com/v1.0/users/{userId}"
                    };

                    await _graphClient!.Groups[groupId].Members.Ref.PostAsync(memberRef);
                    _logger.LogDebug("✅ Membre ajouté: {UserId}", userId);
                }
                catch (ServiceException ex)
                {
                    _logger.LogWarning(ex, "⚠️ Erreur ajout membre {UserId}: {Message}", userId, ex.Message);
                }

            _logger.LogInformation("✅ Ajout membres terminé pour équipe {GroupId}", groupId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur ajout membres équipe {GroupId}", groupId);
            return false;
        }
    }

    /// <summary>
    ///     Récupère l'ID d'un canal par son nom
    /// </summary>
    public async Task<string> GetChannelIdByNameAsync(string teamId, string channelName)
    {
        if (!EnsureEnabled())
            return string.Empty;

        try
        {
            var channels = await _graphClient!.Teams[teamId].Channels.GetAsync();
            var channel = channels.Value.FirstOrDefault(c =>
                c.DisplayName.Equals(channelName, StringComparison.OrdinalIgnoreCase));

            if (channel != null)
            {
                _logger.LogDebug("📺 Canal trouvé '{ChannelName}': {ChannelId}", channelName, channel.Id);
                return channel.Id;
            }

            _logger.LogWarning("📺 Canal '{ChannelName}' non trouvé dans équipe {TeamId}", channelName, teamId);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur recherche canal {ChannelName} dans équipe {TeamId}", channelName, teamId);
            return string.Empty;
        }
    }

    /// <summary>
    ///     Crée un dossier dans les fichiers d'un canal
    /// </summary>
    public async Task CreateFolderInChannelFilesAsync(string teamId, string channelId, string folderName)
    {
        if (!EnsureEnabled())
            return;

        // Votre logique existante avec retry
        var maxRetries = 5;
        var delay = 5000;

        for (var attempt = 0; attempt < maxRetries; attempt++)
            try
            {
                var filesFolder = await _graphClient!.Teams[teamId].Channels[channelId].FilesFolder.GetAsync();

                if (filesFolder?.Id == null)
                {
                    _logger.LogDebug("📁 Dossier non prêt, tentative {Attempt}/{Max}", attempt + 1, maxRetries);
                    await Task.Delay(delay);
                    continue;
                }

                var folder = new DriveItem
                {
                    Name = folderName,
                    Folder = new Folder()
                };

                await _graphClient.Drives[filesFolder.ParentReference.DriveId]
                    .Items[filesFolder.Id]
                    .Children
                    .PostAsync(folder);

                _logger.LogInformation("✅ Dossier '{FolderName}' créé dans canal {ChannelId}", folderName, channelId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Tentative {Attempt}/{Max} échec création dossier '{FolderName}'",
                    attempt + 1, maxRetries, folderName);

                if (attempt == maxRetries - 1)
                    throw;

                await Task.Delay(delay);
            }
    }

    /// <summary>
    ///     Configure les permissions pour un groupe visiteur
    /// </summary>
    public async Task SetReadOnlyPermissionsForVisitorGroupAsync(string teamId, string channelId, string folderName,
        string visitorGroupName, string permission)
    {
        if (!EnsureEnabled())
            return;

        try
        {
            _logger.LogInformation("🔐 Configuration permissions '{Permission}' pour dossier '{FolderName}'",
                permission, folderName);

            // Votre implémentation existante de SetReadOnlyPermissionsForVisitorGroup
            await SetReadOnlyPermissionsForVisitorGroup(teamId, channelId, folderName, visitorGroupName, permission);

            _logger.LogInformation("✅ Permissions configurées pour dossier '{FolderName}'", folderName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur configuration permissions dossier '{FolderName}'", folderName);
            throw;
        }
    }

    /// <summary>
    ///     Vérifie si une équipe existe déjà
    /// </summary>
    public async Task<bool> TeamExistsAsync(string teamName)
    {
        if (!EnsureEnabled())
            return false;

        try
        {
            // Recherche d'équipe par nom - implémentation basique
            var groups = await _graphClient!.Groups.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Filter =
                    $"displayName eq '{teamName}' and resourceProvisioningOptions/any(x:x eq 'Team')";
            });

            return groups?.Value?.Any() == true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur vérification existence équipe '{TeamName}'", teamName);
            return false;
        }
    }

    /// <summary>
    ///     Récupère la liste des membres d'une équipe
    /// </summary>
    public async Task<List<string>> GetTeamMembersAsync(string teamId)
    {
        if (!EnsureEnabled())
            return new List<string>();

        try
        {
            var members = await _graphClient!.Groups[teamId].Members.GetAsync();
            var memberIds = members?.Value?.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).ToList() ??
                            new List<string>();

            _logger.LogDebug("👥 {Count} membres trouvés dans équipe {TeamId}", memberIds.Count, teamId);
            return memberIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur récupération membres équipe {TeamId}", teamId);
            return new List<string>();
        }
    }

    /// <summary>
    ///     Recharge la configuration Teams depuis le fichier
    /// </summary>
    private void ReloadConfiguration()
    {
        _teamsIntegrationConfig = _configService.GetTeamsIntegrationSettingsAsync().GetAwaiter().GetResult();
        _logger.LogDebug("DEBUG: TeamsGroupService - Configuration rechargée. Enabled = {IsEnabled}",
            _teamsIntegrationConfig.Enabled);
        _isEnabled = _teamsIntegrationConfig.Enabled;
    }

    /// <summary>
    ///     Vérifie si le service est actif
    /// </summary>
    private bool EnsureEnabled()
    {
        if (!_isInitialized) ReloadConfiguration();

        if (!_isEnabled || _graphClient == null)
        {
            _logger.LogDebug("🔕 Service Teams non disponible - Opération ignorée");
            return false;
        }

        return true;
    }



    #region Méthodes existantes adaptées

    // Vos méthodes existantes avec logging amélioré et vérification d'activation

    private async Task<string> CreateEducationClassAsync(string className, string classDescription,
        string classMailNickname)
    {
        var educationClass = new EducationClass
        {
            DisplayName = className,
            Description = classDescription,
            MailNickname = classMailNickname,
            ClassCode = "CODE123",
            ExternalId = Guid.NewGuid().ToString(),
            ExternalName = className,
            ExternalSource = EducationExternalSource.Sis
        };

        try
        {
            var createdClass = await _graphClient!.Education.Classes.PostAsync(educationClass);
            _logger.LogInformation("✅ Classe éducation créée: {ClassId}", createdClass.Id);
            return createdClass.Id;
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex, "❌ Erreur création classe éducation '{ClassName}'", className);
            throw;
        }
    }

    private async Task AddTeacherToClassAsync(string classId, string teacherUserId)
    {
        var teacher = await _graphClient!.Education.Users[teacherUserId]
            .GetAsync(requestConfiguration => { requestConfiguration.QueryParameters.Select = new[] { "id" }; });

        if (teacher == null)
            throw new Exception(
                $"L'utilisateur avec l'ID '{teacherUserId}' n'a pas été trouvé en tant que educationUser.");

        var teacherRef = new ReferenceCreate
        {
            OdataId = $"https://graph.microsoft.com/v1.0/education/users/{teacherUserId}"
        };

        await _graphClient.Education.Classes[classId].Teachers.Ref.PostAsync(teacherRef);
        _logger.LogInformation("✅ Enseignant ajouté à la classe: {TeacherId}", teacherUserId);
    }

    private async Task CreateTeamForClassAsync(string classId)
    {
        try
        {
            string groupId = null;
            var maxRetries = 10;
            var delay = 6000;
            var retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var group = await _graphClient!.Education.Classes[classId].Group.GetAsync();

                    if (group != null && group.Id != null)
                    {
                        groupId = group.Id;
                        _logger.LogInformation("✅ GroupId récupéré: {GroupId}", groupId);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("⏳ GroupId non disponible, tentative {Retry}/{Max}", retryCount + 1, maxRetries);
                }

                retryCount++;
                await Task.Delay(delay);
            }

            if (string.IsNullOrEmpty(groupId))
                throw new Exception("Le groupId de la classe est introuvable après plusieurs tentatives.");

            var team = new Team
            {
                MemberSettings = new TeamMemberSettings { AllowCreateUpdateChannels = true },
                MessagingSettings = new TeamMessagingSettings
                {
                    AllowUserEditMessages = true,
                    AllowUserDeleteMessages = true
                },
                FunSettings = new TeamFunSettings
                {
                    AllowGiphy = true,
                    GiphyContentRating = GiphyRatingType.Strict
                }
            };

            await _graphClient!.Groups[groupId].Team.PutAsync(team);
            _logger.LogInformation("✅ Équipe Teams créée pour classe {ClassId}", classId);

            // Création canal par défaut
            await CreateChannelForTeam(groupId, "Histoire", "Canal général pour toute l'équipe");

            // Ajout d'utilisateurs de test
            var userIds = new List<string>
            {
                "c3c59520-6059-440a-b863-b012d4ba1a2a",
                "dd7f34fe-8a0c-44a4-b010-e57ea2208d72"
            };

            await AddMembersToTeam(groupId, userIds);

            // Création des dossiers et permissions
            var channelId = await GetChannelIdByName(groupId, "Histoire");
            if (!string.IsNullOrEmpty(channelId))
            {
                await CreateFolderInChannelFiles(groupId, channelId, "Support de Cours");
                await CreateFolderInChannelFiles(groupId, channelId, "Support");
                await SetReadOnlyPermissionsForVisitorGroup(groupId, channelId, "Eleves", "Membres", "read");
                await SetReadOnlyPermissionsForVisitorGroup(groupId, channelId, "Eleves-Write", "Membres", "write");
            }
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex, "❌ Erreur création équipe pour classe {ClassId}", classId);
            throw;
        }
    }

    // Méthodes existantes de votre code original avec gestion d'activation

    public async Task CreateChannelForTeam(string groupId, string channelName, string channelDescription)
    {
        if (!EnsureEnabled()) return;

        var channel = new Channel
        {
            DisplayName = channelName,
            Description = channelDescription,
            MembershipType = ChannelMembershipType.Standard
        };

        try
        {
            await _graphClient!.Teams[groupId].Channels.PostAsync(channel);
            _logger.LogInformation("✅ Canal '{ChannelName}' créé dans équipe : {GroupId}", channelName, groupId);
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex, "❌ Erreur création canal : {Message}", ex.Message);
            throw;
        }
    }

    public async Task AddMembersToTeam(string groupId, List<string> userIds)
    {
        if (!EnsureEnabled()) return;

        foreach (var userId in userIds)
            try
            {
                var memberRef = new ReferenceCreate
                {
                    OdataId = $"https://graph.microsoft.com/v1.0/users/{userId}"
                };

                await _graphClient!.Groups[groupId].Members.Ref.PostAsync(memberRef);
                _logger.LogInformation("✅ Membre ajouté : {UserId}", userId);
            }
            catch (ServiceException ex)
            {
                _logger.LogWarning("⚠️ Erreur ajout membre {UserId} : {Message}", userId, ex.Message);
            }
    }

    public async Task<string> GetChannelIdByName(string teamId, string channelName)
    {
        if (!EnsureEnabled()) return string.Empty;

        try
        {
            var channels = await _graphClient!.Teams[teamId].Channels.GetAsync();
            var channel = channels.Value.FirstOrDefault(c =>
                c.DisplayName.Equals(channelName, StringComparison.OrdinalIgnoreCase));

            if (channel != null)
            {
                _logger.LogInformation("✅ ID du canal '{ChannelName}' : {ChannelId}", channelName, channel.Id);
                return channel.Id;
            }

            _logger.LogWarning("⚠️ Canal '{ChannelName}' introuvable", channelName);
            return string.Empty;
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex, "❌ Erreur récupération ID canal : {Message}", ex.Message);
            return string.Empty;
        }
    }

    public async Task CreateFolderInChannelFiles(string teamId, string channelId, string folderName)
    {
        if (!EnsureEnabled()) return;

        var maxRetries = 5;
        var delay = 5000;

        for (var attempt = 0; attempt < maxRetries; attempt++)
            try
            {
                var filesFolder = await _graphClient!.Teams[teamId].Channels[channelId].FilesFolder.GetAsync();

                if (filesFolder?.Id == null)
                {
                    _logger.LogDebug("📁 Dossier non prêt, tentative {Attempt}", attempt + 1);
                    await Task.Delay(delay);
                    continue;
                }

                var folder = new DriveItem
                {
                    Name = folderName,
                    Folder = new Folder()
                };

                await _graphClient.Drives[filesFolder.ParentReference.DriveId]
                    .Items[filesFolder.Id]
                    .Children
                    .PostAsync(folder);

                _logger.LogInformation("✅ Dossier '{FolderName}' créé", folderName);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Tentative {Attempt} échec", attempt + 1);
                if (attempt == maxRetries - 1)
                    throw;

                await Task.Delay(delay);
            }
    }

    public async Task SetReadOnlyPermissionsForVisitorGroup(string teamId, string channelId, string folderName,
        string visitorGroupName, string perm)
    {
        if (!EnsureEnabled()) return;

        try
        {
            var channelFilesFolder = await _graphClient!.Teams[teamId].Channels[channelId].FilesFolder.GetAsync();

            var driveId = channelFilesFolder.ParentReference.DriveId;
            var folder = new DriveItem
            {
                Name = folderName,
                Folder = new Folder()
            };

            var createdFolder = await _graphClient.Drives[driveId].Items[channelFilesFolder.Id]
                .Children
                .PostAsync(folder);

            _logger.LogInformation("✅ Dossier '{FolderName}' créé avec ID: {FolderId}", folderName, createdFolder.Id);

            await Task.Delay(5000);

            var permissions = await _graphClient.Drives[driveId].Items[channelFilesFolder.Id].Permissions.GetAsync();

            string siteGroupId = null;
            string siteGroupName = null;

            foreach (var permission in permissions.Value)
                if (permission.GrantedToV2?.SiteGroup != null &&
                    permission.GrantedToV2.SiteGroup.DisplayName.Contains(visitorGroupName))
                {
                    siteGroupId = permission.GrantedToV2.SiteGroup.Id;
                    siteGroupName = permission.GrantedToV2.SiteGroup.DisplayName;
                    _logger.LogInformation("✅ Groupe trouvé: ID = {SiteGroupId}, Nom = {SiteGroupName}", siteGroupId,
                        siteGroupName);
                    break;
                }

            if (!string.IsNullOrEmpty(siteGroupId))
                await SetFolderPermissionsForSiteMembers(driveId, createdFolder.Id, siteGroupId, siteGroupName, perm);
            else
                _logger.LogWarning("⚠️ Groupe '{VisitorGroupName}' non trouvé", visitorGroupName);
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex, "❌ Erreur Graph API: {Message}", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur inattendue: {Message}", ex.Message);
            throw;
        }
    }

    public async Task SetFolderPermissionsForSiteMembers(string driveId, string folderId, string siteGroupId,
        string siteGroupName, string permissionType = "read")
    {
        if (!EnsureEnabled()) return;

        try
        {
            var permissions = await _graphClient!.Drives[driveId].Items[folderId].Permissions.GetAsync();

            var existingPermission = permissions.Value.FirstOrDefault(p =>
                p.GrantedToV2?.SiteGroup?.Id == siteGroupId);

            if (existingPermission != null)
            {
                _logger.LogInformation("🔄 Mise à jour permission existante");
                existingPermission.Roles = new List<string> { permissionType.ToLower() == "write" ? "write" : "read" };

                await _graphClient.Drives[driveId].Items[folderId].Permissions[existingPermission.Id]
                    .PatchAsync(existingPermission);

                _logger.LogInformation("✅ Permission mise à jour pour groupe '{SiteGroupName}'", siteGroupName);
            }
            else
            {
                _logger.LogInformation("➕ Création nouvelle permission");
                var newPermission = new Permission
                {
                    Roles = new List<string> { permissionType.ToLower() == "write" ? "write" : "read" },
                    GrantedToV2 = new SharePointIdentitySet
                    {
                        SiteGroup = new SharePointIdentity
                        {
                            Id = siteGroupId,
                            DisplayName = siteGroupName
                        }
                    }
                };

                await _graphClient.Drives[driveId].Items[folderId].Permissions.PostAsync(newPermission);
                _logger.LogInformation("✅ Permissions '{PermissionType}' créées pour dossier '{FolderId}'",
                    permissionType, folderId);
            }
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex, "❌ Erreur Graph API: {Message}", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur inattendue: {Message}", ex.Message);
            throw;
        }
    }

    #endregion
}