using System.Text.RegularExpressions;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;

namespace ADManagerAPI.Services.Teams;

/// <summary>
///     Service principal pour l'intégration entre les OUs Active Directory et Microsoft Teams
///     RENDU OPTIONNEL - ne fonctionne que si l'intégration Teams est activée
/// </summary>
public class TeamsIntegrationService : ITeamsIntegrationService
{
    private readonly TimeSpan _configReloadInterval = TimeSpan.FromMinutes(5);
    private readonly IConfigService _configService;
    private readonly SemaphoreSlim _creationSemaphore;
    private readonly bool _isInitialized;
    private readonly ILdapService _ldapService;
    private readonly ILogger<TeamsIntegrationService> _logger;
    private readonly ITeamsGroupService? _teamsService;
    private GraphApiConfig _graphApiConfig;
    private bool _isEnabled;
    private DateTime _lastConfigReload = DateTime.MinValue;
    private TeamsIntegrationConfig _teamsIntegrationConfig;

    public TeamsIntegrationService(
        ILdapService ldapService,
        ILogger<TeamsIntegrationService> logger,
        IConfigService configService,
        ITeamsGroupService? teamsService = null)
    {
        _ldapService = ldapService ?? throw new ArgumentNullException(nameof(ldapService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));

        // Charger la configuration Teams au démarrage UNE SEULE FOIS
        ReloadConfiguration();

        if (!_isEnabled)
        {
            _logger.LogInformation("🔕 Intégration Teams désactivée - Service en mode stub");
            _teamsService = null;
            _creationSemaphore = new SemaphoreSlim(1, 1);
            _isInitialized = true;
            return;
        }

        _teamsService = teamsService;

        if (_teamsService == null)
        {
            _logger.LogWarning("⚠️ Services Teams manquants - Intégration désactivée");
            _isEnabled = false;
        }

        _creationSemaphore = new SemaphoreSlim(3, 3);

        _logger.LogInformation(_isEnabled
            ? "✅ TeamsIntegrationService initialisé et activé"
            : "🔕 TeamsIntegrationService en mode désactivé");

        _isInitialized = true;
    }

    /// <summary>
    ///     Crée une équipe Teams à partir d'une OU nouvellement créée avec configuration d'import
    /// </summary>
    public async Task<TeamsCreationResult> CreateTeamFromOUAsync(string ouName, string ouPath, string? teacherId = null,
        string? importId = null)
    {
        var result = new TeamsCreationResult
        {
            ClassName = ouName,
            OUPath = ouPath
        };

        // ✅ CORRECTION : Si c'est appelé depuis un import, contourner la vérification globale
        if (string.IsNullOrEmpty(importId) && !EnsureEnabled())
        {
            result.ErrorMessage = "Intégration Teams désactivée";
            result.Success = false;
            _logger.LogDebug("🔕 Création Teams ignorée pour OU '{OUName}' - Service désactivé", ouName);
            return result;
        }
        
        // Si c'est un import, vérifier que les services Teams sont disponibles
        if (!string.IsNullOrEmpty(importId) && _teamsService == null)
        {
            result.ErrorMessage = "Service Teams non disponible";
            result.Success = false;
            _logger.LogDebug("🔕 Création Teams ignorée pour OU '{OUName}' - Service Teams non disponible", ouName);
            return result;
        }

        _logger.LogInformation("🚀 Début création équipe Teams pour OU '{OUName}' ({OUPath})", ouName, ouPath);

        try
        {
            // Récupérer la configuration Teams (soit d'import, soit globale)
            TeamsImportConfig? importConfig = null;
            if (!string.IsNullOrEmpty(importId))
            {
                var teamsImportService = new TeamsImportConfigService(_logger, _configService);
                importConfig = await teamsImportService.GetTeamsConfigForImportAsync(importId);
            }

            // ✅ FALLBACK : Si aucune configuration d'import trouvée, créer une configuration par défaut activée
            if (importConfig == null)
            {
                _logger.LogInformation("⚠️ Aucune configuration Teams d'import trouvée pour '{ImportId}', utilisation d'une configuration par défaut", importId ?? "null");
                var teamsImportService = new TeamsImportConfigService(_logger, _configService);
                importConfig = teamsImportService.CreateDefaultTeamsConfig();
                importConfig.Enabled = true; // Forcer l'activation pour l'exécution
                
                // Utiliser les paramètres par défaut de la configuration globale Teams si disponible
                if (_teamsIntegrationConfig.Enabled)
                {
                    _logger.LogInformation("✅ Configuration Teams globale activée, utilisation pour l'import");
                }
            }

            // 1. Vérifications préalables - Vérifier si l'intégration Teams est activée
            if (!importConfig.Enabled)
            {
                result.ErrorMessage = "Intégration Teams désactivée";
                _logger.LogInformation("⏭️ Intégration Teams désactivée pour OU '{OUName}'", ouName);
                return result;
            }

            if (IsOUExcluded(ouPath))
            {
                result.ErrorMessage = "OU exclue de la création Teams";
                _logger.LogInformation("⏭️ OU '{OUName}' exclue de la création Teams", ouName);
                return result;
            }

            // 2. Vérifier si une équipe existe déjà pour cette classe spécifique
            // Utiliser la combinaison OU + ClassName pour identifier de manière unique chaque équipe
            var existingMapping = _teamsIntegrationConfig.Mappings.FirstOrDefault(m =>
                m.OUDistinguishedName.Equals(ouPath, StringComparison.OrdinalIgnoreCase) &&
                m.ClassName.Equals(ouName, StringComparison.OrdinalIgnoreCase));
            if (existingMapping != null)
            {
                result.ErrorMessage = "Équipe Teams déjà existante pour cette classe";
                result.TeamId = existingMapping.TeamId;
                result.GroupId = existingMapping.GroupId;
                _logger.LogWarning("⚠️ Équipe Teams déjà créée pour classe '{ClassName}' dans OU '{OUPath}': {TeamId}", 
                    ouName, ouPath, existingMapping.TeamId);
                return result;
            }

            // 3. Déterminer l'enseignant responsable
            var resolvedTeacherId = ResolveTeacherId(ouPath, teacherId, importConfig);
            if (string.IsNullOrEmpty(resolvedTeacherId))
            {
                result.Warnings.Add("Aucun enseignant spécifié");
                result.ErrorMessage = "Aucun enseignant disponible pour créer l'équipe";
                _logger.LogError("❌ Impossible de créer l'équipe '{OUName}': aucun enseignant disponible", ouName);
                return result;
            }

            // 4. Préparer les paramètres de création
            var teamName = GenerateTeamName(ouName, importConfig);
            var teamDescription = GenerateTeamDescription(ouName, importConfig);
            var mailNickname = GenerateMailNickname(ouName);

            _logger.LogInformation(
                "📋 Création équipe Teams: Nom='{TeamName}', Description='{Description}', Enseignant={TeacherId}",
                teamName, teamDescription, resolvedTeacherId);

            // 5. Créer l'équipe Teams avec retry et limitation de débit
            await _creationSemaphore.WaitAsync();
            try
            {
                var teamId = await CreateTeamWithRetryAsync(teamName, teamDescription, mailNickname, resolvedTeacherId);

                if (!string.IsNullOrEmpty(teamId))
                {
                    result.Success = true;
                    result.TeamId = teamId;
                    result.GroupId = teamId;

                    // 6. Enregistrer le mapping
                    var newMapping = new OUTeamsMapping
                    {
                        OUDistinguishedName = ouPath,
                        TeamId = teamId,
                        GroupId = teamId,
                        ClassName = ouName,
                        CreatedAt = DateTime.UtcNow,
                        LastSyncAt = DateTime.UtcNow,
                        IsActive = true,
                        ImportId = importId
                    };
                    _teamsIntegrationConfig.Mappings.Add(newMapping);
                    await _configService.UpdateTeamsIntegrationSettingsAsync(_teamsIntegrationConfig);

                    // 7. Créer les dossiers Teams si configurés
                    if (importConfig?.FolderMappings?.Any() == true)
                        await CreateTeamsFoldersAsync(teamId, importConfig.FolderMappings, result);

                    // 8. Synchroniser les utilisateurs existants de l'OU
                    await SyncExistingUsersAsync(ouPath, teamId, result);

                    _logger.LogInformation("✅ Équipe Teams créée avec succès pour OU '{OUName}': {TeamId}", ouName,
                        teamId);
                }
                else
                {
                    result.ErrorMessage = "La création de l'équipe Teams a échoué (ID null)";
                    _logger.LogError("❌ Échec création équipe Teams pour OU '{OUName}': ID null retourné", ouName);
                }
            }
            finally
            {
                _creationSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Erreur lors de la création: {ex.Message}";
            _logger.LogError(ex, "❌ Erreur lors de la création d'équipe Teams pour OU '{OUName}'", ouName);
        }

        return result;
    }

    /// <summary>
    ///     Ajoute un utilisateur à l'équipe Teams correspondant à son OU
    /// </summary>
    public async Task<bool> AddUserToOUTeamAsync(string samAccountName, string ouDn)
    {
        if (!EnsureEnabled())
        {
            _logger.LogDebug("🔕 Ajout utilisateur Teams ignoré pour {User} - Service désactivé", samAccountName);
            return false;
        }

        try
        {
            var mapping = _teamsIntegrationConfig.Mappings.FirstOrDefault(m =>
                m.OUDistinguishedName.Equals(ouDn, StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
            {
                _logger.LogDebug("ℹ️ Aucune équipe Teams trouvée pour OU '{OUDN}' (utilisateur: {User})", ouDn,
                    samAccountName);
                return false;
            }

            var autoAddUsers = false;
            if (!string.IsNullOrEmpty(mapping.ImportId))
            {
                var teamsImportService = new TeamsImportConfigService(_logger, _configService);
                var importConfig = await teamsImportService.GetTeamsConfigForImportAsync(mapping.ImportId);
                autoAddUsers = importConfig?.AutoAddUsersToTeams ?? false;
            }

            if (!autoAddUsers)
            {
                _logger.LogDebug("⏭️ Ajout automatique utilisateurs Teams désactivé pour {User}", samAccountName);
                return false;
            }

            var user = await _ldapService.GetUserAsync(samAccountName);
            if (user == null)
            {
                _logger.LogWarning("⚠️ Utilisateur '{User}' non trouvé dans AD pour ajout à Teams", samAccountName);
                return false;
            }

            var userIds = new List<string> { GetUserIdFromUser(user) };
            var success = await _teamsService!.AddMembersToTeamAsync(mapping.GroupId, userIds);

            if (success)
            {
                _logger.LogInformation("✅ Utilisateur '{User}' ajouté à l'équipe Teams '{TeamId}'", samAccountName,
                    mapping.TeamId);

                mapping.MemberCount = mapping.MemberCount + userIds.Count;
                mapping.LastSyncAt = DateTime.UtcNow;
                await _configService.UpdateTeamsIntegrationSettingsAsync(_teamsIntegrationConfig);
            }
            else
            {
                _logger.LogWarning("⚠️ Échec ajout utilisateur '{User}' à l'équipe Teams '{TeamId}'", samAccountName,
                    mapping.TeamId);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de l'ajout utilisateur '{User}' à Teams (OU: {OUDN})", samAccountName,
                ouDn);
            return false;
        }
    }

    /// <summary>
    ///     Synchronise tous les utilisateurs d'une OU vers l'équipe Teams correspondante
    /// </summary>
    public async Task<bool> SyncOUUsersToTeamAsync(string ouDn)
    {
        if (!EnsureEnabled())
        {
            _logger.LogDebug("🔕 Synchronisation Teams ignorée pour OU '{OUDN}' - Service désactivé", ouDn);
            return false;
        }

        try
        {
            _logger.LogInformation("🔄 Début synchronisation utilisateurs OU '{OUDN}' vers Teams", ouDn);

            var mapping = _teamsIntegrationConfig.Mappings.FirstOrDefault(m =>
                m.OUDistinguishedName.Equals(ouDn, StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
            {
                _logger.LogWarning("⚠️ Aucune équipe Teams trouvée pour OU '{OUDN}'", ouDn);
                return false;
            }

            var adUsers = await _ldapService.GetAllUsersInOuAsync(ouDn);
            if (!adUsers.Any())
            {
                _logger.LogInformation("ℹ️ Aucun utilisateur trouvé dans OU '{OUDN}'", ouDn);
                return true;
            }

            var currentTeamMembers = await _teamsService!.GetTeamMembersAsync(mapping.TeamId);

            var usersToAdd = new List<string>();
            foreach (var user in adUsers)
            {
                var userId = GetUserIdFromUser(user);
                if (!string.IsNullOrEmpty(userId) && !currentTeamMembers.Contains(userId)) usersToAdd.Add(userId);
            }

            if (usersToAdd.Any())
            {
                var success = await _teamsService.AddMembersToTeamAsync(mapping.GroupId, usersToAdd);
                if (success)
                {
                    _logger.LogInformation("✅ {Count} utilisateurs ajoutés à l'équipe Teams '{TeamId}'",
                        usersToAdd.Count, mapping.TeamId);
                }
                else
                {
                    _logger.LogWarning("⚠️ Échec ajout de {Count} utilisateurs à l'équipe Teams '{TeamId}'",
                        usersToAdd.Count, mapping.TeamId);
                    return false;
                }
            }
            else
            {
                _logger.LogInformation("ℹ️ Tous les utilisateurs sont déjà dans l'équipe Teams '{TeamId}'",
                    mapping.TeamId);
            }

            mapping.MemberCount = adUsers.Count;
            mapping.LastSyncAt = DateTime.UtcNow;
            await _configService.UpdateTeamsIntegrationSettingsAsync(_teamsIntegrationConfig);

            _logger.LogInformation("✅ Synchronisation terminée pour OU '{OUDN}': {Count} utilisateurs", ouDn,
                adUsers.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la synchronisation OU '{OUDN}' vers Teams", ouDn);
            return false;
        }
    }

    /// <summary>
    ///     Migre toutes les OUs existantes vers Teams
    /// </summary>
    public async Task<List<TeamsCreationResult>> MigrateExistingOUsAsync()
    {
        var results = new List<TeamsCreationResult>();

        if (!EnsureEnabled())
        {
            _logger.LogInformation("🔕 Migration Teams ignorée - Service désactivé");
            return results;
        }

        try
        {
            _logger.LogInformation("🚀 Début migration des OUs existantes vers Teams");

            var allOUs = await _ldapService.GetAllOrganizationalUnitsAsync();
            _logger.LogInformation("📊 {Count} OUs trouvées pour migration", allOUs.Count);

            var ousToMigrate = new List<OrganizationalUnitModel>();
            foreach (var ou in allOUs)
            {
                if (IsOUExcluded(ou.DistinguishedName))
                {
                    _logger.LogDebug("⏭️ OU exclue: {OUDN}", ou.DistinguishedName);
                    continue;
                }

                var hasMapping = _teamsIntegrationConfig.Mappings.Any(m =>
                    m.OUDistinguishedName.Equals(ou.DistinguishedName, StringComparison.OrdinalIgnoreCase));
                if (!hasMapping) ousToMigrate.Add(ou);
            }

            _logger.LogInformation("📋 {Count} OUs à migrer vers Teams", ousToMigrate.Count);

            var semaphore = new SemaphoreSlim(2, 2);
            var tasks = ousToMigrate.Select(async ou =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var ouName = ExtractOUName(ou.DistinguishedName);
                    var result = await CreateTeamFromOUAsync(ouName, ou.DistinguishedName);

                    await Task.Delay(2000);

                    return result;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            results = (await Task.WhenAll(tasks)).ToList();

            var successful = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);

            _logger.LogInformation("✅ Migration terminée: {Successful} succès, {Failed} échecs sur {Total} OUs",
                successful, failed, results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la migration des OUs existantes");
        }

        return results;
    }

    /// <summary>
    ///     Vérifie la santé de l'intégration Teams
    /// </summary>
    public async Task<TeamsIntegrationHealthStatus> GetHealthStatusAsync()
    {
        var status = new TeamsIntegrationHealthStatus
        {
            Enabled = _isEnabled
        };

        try
        {
            status.LdapConnected = _ldapService.IsLdapHealthy();

            if (_isEnabled && _teamsService != null)
                try
                {
                    await _teamsService.TeamExistsAsync("HealthCheckTeam");
                    status.GraphApiConnected = true;
                }
                catch
                {
                    status.GraphApiConnected = false;
                }
            else
                status.GraphApiConnected = false;

            if (_isEnabled)
            {
                status.ActiveMappingsCount = _teamsIntegrationConfig.Mappings.Count(m => m.IsActive);
                status.Metrics["TotalMappings"] = status.ActiveMappingsCount;
            }

            if (!_isEnabled)
            {
                status.IsHealthy = true;
                status.Status = "Disabled";
                status.Issues.Add("Intégration Teams volontairement désactivée");
            }
            else
            {
                status.IsHealthy = status.LdapConnected && status.GraphApiConnected;
                status.Status = status.IsHealthy ? "Healthy" : "Degraded";

                if (!status.LdapConnected)
                    status.Issues.Add("LDAP connection failed");
                if (!status.GraphApiConnected)
                    status.Issues.Add("Graph API connection failed");
            }

            status.Metrics["LdapHealthy"] = status.LdapConnected;
            status.Metrics["GraphApiHealthy"] = status.GraphApiConnected;
            status.Metrics["Enabled"] = _isEnabled;
        }
        catch (Exception ex)
        {
            status.IsHealthy = false;
            status.Status = "Error";
            status.Issues.Add($"Health check failed: {ex.Message}");
            _logger.LogError(ex, "❌ Erreur lors du health check Teams integration");
        }

        return status;
    }

    /// <summary>
    ///     Resynchronise manuellement une OU spécifique
    /// </summary>
    public async Task<TeamsCreationResult> ResyncOUToTeamAsync(string ouDn)
    {
        if (!EnsureEnabled())
            return new TeamsCreationResult
            {
                Success = false,
                OUPath = ouDn,
                ErrorMessage = "Intégration Teams désactivée"
            };

        _logger.LogInformation("🔄 Début resynchronisation manuelle OU '{OUDN}'", ouDn);

        try
        {
            var syncSuccess = await SyncOUUsersToTeamAsync(ouDn);

            var result = new TeamsCreationResult
            {
                Success = syncSuccess,
                OUPath = ouDn,
                ClassName = ExtractOUName(ouDn)
            };

            if (!syncSuccess) result.ErrorMessage = "Échec de la synchronisation des utilisateurs";

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la resynchronisation OU '{OUDN}'", ouDn);
            return new TeamsCreationResult
            {
                Success = false,
                OUPath = ouDn,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Recharge la configuration Teams depuis le fichier
    /// </summary>
    private void ReloadConfiguration()
    {
        _teamsIntegrationConfig = _configService.GetTeamsIntegrationSettingsAsync().GetAwaiter().GetResult();
        _graphApiConfig = new GraphApiConfig
            { MaxRetryAttempts = 3, RetryDelayMs = 1000 }; // TODO: Récupérer depuis _configService
        _lastConfigReload = DateTime.Now;
        _logger.LogDebug("DEBUG: TeamsIntegrationService - Configuration rechargée. Enabled = {IsEnabled}",
            _teamsIntegrationConfig.Enabled);
        _isEnabled = _teamsIntegrationConfig.Enabled;
    }

    /// <summary>
    ///     Vérifie si l'intégration Teams est activée
    /// </summary>
    private bool EnsureEnabled()
    {
        if (!_isInitialized || DateTime.Now - _lastConfigReload > _configReloadInterval) ReloadConfiguration();

        if (!_isEnabled || _teamsService == null)
        {
            _logger.LogDebug("🔕 Intégration Teams désactivée - Opération ignorée");
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        _creationSemaphore?.Dispose();
    }

    #region Méthodes privées utilitaires

    private bool IsOUExcluded(string ouPath)
    {
        return _teamsIntegrationConfig.ExcludedOUs.Any(excluded =>
            ouPath.Contains(excluded, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveTeacherId(string ouPath, string? providedTeacherId, TeamsImportConfig? importConfig = null)
    {
        if (!string.IsNullOrEmpty(providedTeacherId))
            return providedTeacherId;

        /*if (importConfig?.OUTeacherMappings != null)
        {
            var mapping = importConfig.OUTeacherMappings.FirstOrDefault(kvp =>
                ouPath.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(mapping.Value))
                return mapping.Value;
        }*/

        if (!string.IsNullOrEmpty(importConfig?.DefaultTeacherUserId))
            return importConfig.DefaultTeacherUserId;

        return null;
    }

    private string GenerateTeamName(string ouName, TeamsImportConfig? importConfig = null)
    {
        var template = importConfig?.TeamNamingTemplate ?? "Classe {OUName}";
        return template.Replace("{OUName}", ouName);
    }

    private string GenerateTeamDescription(string ouName, TeamsImportConfig? importConfig = null)
    {
        var template = importConfig?.TeamDescriptionTemplate ?? "Équipe pour la classe {OUName}";
        return template.Replace("{OUName}", ouName);
    }

    private string GenerateMailNickname(string ouName)
    {
        var nickname = Regex.Replace(ouName.ToLower(), @"[^a-z0-9]", "");
        return nickname.Length > 20 ? nickname.Substring(0, 20) : nickname;
    }

    private string GenerateUniqueMailNickname(string baseNickname, int attempt)
    {
        var suffix = attempt.ToString();
        var maxBaseLength = 20 - suffix.Length; // Garder de la place pour le suffixe
        
        var truncatedBase = baseNickname.Length > maxBaseLength 
            ? baseNickname.Substring(0, maxBaseLength) 
            : baseNickname;
            
        return $"{truncatedBase}{suffix}";
    }

    private string ExtractOUName(string ouDn)
    {
        var match = Regex.Match(ouDn, @"^OU=([^,]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : ouDn;
    }

    private string GetUserIdFromUser(UserModel user)
    {
        return user.UserPrincipalName ?? user.SamAccountName;
    }

    private async Task<string?> CreateTeamWithRetryAsync(string teamName, string description, string mailNickname,
        string teacherId)
    {
        var attemptCount = 0;
        var maxAttempts = _graphApiConfig.MaxRetryAttempts;
        var currentMailNickname = mailNickname;

        while (attemptCount < maxAttempts)
        {
            attemptCount++;
            _logger.LogDebug("🔄 Tentative {Attempt}/{Max} création équipe '{TeamName}' avec mailNickname '{MailNickname}'", 
                attemptCount, maxAttempts, teamName, currentMailNickname);

            try
            {
                var teamId = await _teamsService!.CreateClassTeamAsync(teamName, description, currentMailNickname, teacherId);

                if (!string.IsNullOrEmpty(teamId))
                {
                    _logger.LogInformation("✅ Équipe créée avec succès: '{TeamName}' avec mailNickname '{MailNickname}' (tentative {Attempt})", 
                        teamName, currentMailNickname, attemptCount);
                    return teamId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Échec tentative {Attempt}/{Max} création équipe '{TeamName}': {Error}",
                    attemptCount, maxAttempts, teamName, ex.Message);

                // Si c'est un conflit de mailNickname et qu'il reste des tentatives, générer un nouveau nom unique
                if (ex.Message.Contains("Another object with the same value for property mailNickname already exists", StringComparison.OrdinalIgnoreCase) && attemptCount < maxAttempts)
                {
                    currentMailNickname = GenerateUniqueMailNickname(mailNickname, attemptCount);
                    _logger.LogInformation("🔄 Conflit mailNickname détecté, nouveau nom généré: '{NewMailNickname}'", currentMailNickname);
                    continue; // Retry immédiatement avec le nouveau mailNickname
                }

                // Pour les autres erreurs ou si c'est la dernière tentative
                if (attemptCount >= maxAttempts)
                {
                    break;
                }

                // Attendre avant la prochaine tentative pour les autres types d'erreurs
                var delay = _graphApiConfig.RetryDelayMs * attemptCount;
                _logger.LogDebug("⏳ Attente {Delay}ms avant nouvelle tentative", delay);
                await Task.Delay(delay);
            }
        }

        _logger.LogError("❌ Échec définitif création équipe '{TeamName}' après {Attempts} tentatives", teamName,
            maxAttempts);
        return null;
    }

    private async Task SyncExistingUsersAsync(string ouPath, string teamId, TeamsCreationResult result)
    {
        try
        {
            var users = await _ldapService.GetAllUsersInOuAsync(ouPath);
            if (users.Any())
            {
                var userIds = users.Select(GetUserIdFromUser).Where(id => !string.IsNullOrEmpty(id)).ToList();
                if (userIds.Any())
                {
                    var success = await _teamsService!.AddMembersToTeamAsync(teamId, userIds);
                    if (success)
                    {
                        result.AdditionalInfo["UsersAdded"] = userIds.Count;
                        _logger.LogInformation("✅ {Count} utilisateurs existants ajoutés à l'équipe {TeamId}",
                            userIds.Count, teamId);
                    }
                    else
                    {
                        result.Warnings.Add($"Échec ajout de {userIds.Count} utilisateurs existants");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Erreur synchronisation utilisateurs existants: {ex.Message}");
            _logger.LogWarning(ex,
                "⚠️ Erreur lors de la synchronisation des utilisateurs existants pour équipe {TeamId}", teamId);
        }
    }

    /// <summary>
    ///     Crée les dossiers Teams selon la configuration
    /// </summary>
    private async Task CreateTeamsFoldersAsync(string teamId, List<TeamsFolderMapping> folderMappings,
        TeamsCreationResult result)
    {
        try
        {
            var createdFolders = new List<string>();

            foreach (var folder in folderMappings.Where(f => f.Enabled).OrderBy(f => f.Order))
                try
                {
                    _logger.LogInformation("📁 Création dossier Teams '{FolderName}' pour équipe {TeamId}",
                        folder.FolderName, teamId);

                    // TODO: Implémenter la création via TeamsGroupService
                    // await _teamsService.CreateTeamsFolderAsync(teamId, folder);
                    createdFolders.Add(folder.FolderName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Échec création dossier '{FolderName}' pour équipe {TeamId}",
                        folder.FolderName, teamId);
                    result.Warnings.Add($"Échec création dossier '{folder.FolderName}': {ex.Message}");
                }

            result.CreatedFolders = createdFolders;
            _logger.LogInformation("✅ {Count} dossiers Teams créés pour équipe {TeamId}", createdFolders.Count, teamId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la création des dossiers Teams pour équipe {TeamId}", teamId);
            result.Warnings.Add($"Erreur création dossiers: {ex.Message}");
        }
    }

    #endregion
}