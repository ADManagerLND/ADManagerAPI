using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace ADManagerAPI.Services.Teams
{
    /// <summary>
    /// Service principal pour l'intégration entre les OUs Active Directory et Microsoft Teams
    /// RENDU OPTIONNEL - ne fonctionne que si l'intégration Teams est activée
    /// </summary>
    public class TeamsIntegrationService : ITeamsIntegrationService
    {
        private readonly ILdapService _ldapService;
        private readonly ITeamsGroupService? _teamsService;
        private readonly ILogger<TeamsIntegrationService> _logger;
        private readonly IConfigService _configService;
        private TeamsIntegrationConfig _teamsIntegrationConfig;
        private readonly SemaphoreSlim _creationSemaphore;
        private bool _isEnabled;
        private bool _isInitialized = false; // 🆕 NOUVEAU : Évite les réinitialisations
        
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
                _isInitialized = true; // 🆕 Marquer comme initialisé
                return;
            }

            _teamsService = teamsService;
            
            if (_teamsService == null)
            {
                _logger.LogWarning("⚠️ Services Teams manquants - Intégration désactivée");
                _isEnabled = false;
            }
            
            _creationSemaphore = new SemaphoreSlim(3, 3); // Limite à 3 créations simultanées
            
            _logger.LogInformation(_isEnabled 
                ? "✅ TeamsIntegrationService initialisé et activé" 
                : "🔕 TeamsIntegrationService en mode désactivé");
                
            _isInitialized = true; // 🆕 Marquer comme initialisé
        }

        /// <summary>
        /// Recharge la configuration Teams depuis le fichier
        /// </summary>
        private void ReloadConfiguration()
        {
            _teamsIntegrationConfig = _configService.GetTeamsIntegrationSettingsAsync().GetAwaiter().GetResult();
            _lastConfigReload = DateTime.Now; // Mettre à jour le timestamp
            _logger.LogDebug("DEBUG: TeamsIntegrationService - Configuration rechargée. Enabled = {IsEnabled}", _teamsIntegrationConfig.Enabled);
            _isEnabled = _teamsIntegrationConfig.Enabled;
        }

        private DateTime _lastConfigReload = DateTime.MinValue;
        private readonly TimeSpan _configReloadInterval = TimeSpan.FromMinutes(5); // Recharger max toutes les 5 minutes
        
        /// <summary>
        /// Vérifie si l'intégration Teams est activée
        /// </summary>
        private bool EnsureEnabled()
        {
            // 🆕 OPTIMISATION : Ne recharger la config que si pas encore initialisé OU si intervalle dépassé
            if (!_isInitialized || DateTime.Now - _lastConfigReload > _configReloadInterval)
            {
                ReloadConfiguration();
            }
            
            if (!_isEnabled || _teamsService == null)
            {
                _logger.LogDebug("🔕 Intégration Teams désactivée - Opération ignorée");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Crée une équipe Teams à partir d'une OU nouvellement créée
        /// </summary>
        public async Task<TeamsCreationResult> CreateTeamFromOUAsync(string ouName, string ouPath, string? teacherId = null)
        {
            var result = new TeamsCreationResult
            {
                ClassName = ouName,
                OUPath = ouPath
            };

            if (!EnsureEnabled())
            {
                result.ErrorMessage = "Intégration Teams désactivée";
                result.Success = false;
                _logger.LogDebug("🔕 Création Teams ignorée pour OU '{OUName}' - Service désactivé", ouName);
                return result;
            }

            _logger.LogInformation("🚀 Début création équipe Teams pour OU '{OUName}' ({OUPath})", ouName, ouPath);

            try
            {
                // 1. Vérifications préalables
                if (!_teamsIntegrationConfig.AutoCreateTeamsForOUs)
                {
                    result.ErrorMessage = "Création automatique Teams désactivée";
                    _logger.LogInformation("⏭️ Création Teams désactivée pour OU '{OUName}'", ouName);
                    return result;
                }

                if (IsOUExcluded(ouPath))
                {
                    result.ErrorMessage = "OU exclue de la création Teams";
                    _logger.LogInformation("⏭️ OU '{OUName}' exclue de la création Teams", ouName);
                    return result;
                }

                // 2. Vérifier si une équipe existe déjà
                var existingMapping = _teamsIntegrationConfig.Mappings.FirstOrDefault(m => m.OUDistinguishedName.Equals(ouPath, StringComparison.OrdinalIgnoreCase));
                if (existingMapping != null)
                {
                    result.ErrorMessage = "Équipe Teams déjà existante pour cette OU";
                    result.TeamId = existingMapping.TeamId;
                    result.GroupId = existingMapping.GroupId;
                    _logger.LogWarning("⚠️ Équipe Teams déjà créée pour OU '{OUName}': {TeamId}", ouName, existingMapping.TeamId);
                    return result;
                }

                // 3. Déterminer l'enseignant responsable
                var resolvedTeacherId = ResolveTeacherId(ouPath, teacherId);
                if (string.IsNullOrEmpty(resolvedTeacherId))
                {
                    result.Warnings.Add("Aucun enseignant spécifié, utilisation de l'enseignant par défaut");
                    resolvedTeacherId = _teamsIntegrationConfig.DefaultTeacherUserId;
                }

                if (string.IsNullOrEmpty(resolvedTeacherId))
                {
                    result.ErrorMessage = "Aucun enseignant disponible pour créer l'équipe";
                    _logger.LogError("❌ Impossible de créer l'équipe '{OUName}': aucun enseignant disponible", ouName);
                    return result;
                }

                // 4. Préparer les paramètres de création
                var teamName = GenerateTeamName(ouName);
                var teamDescription = GenerateTeamDescription(ouName);
                var mailNickname = GenerateMailNickname(ouName);

                _logger.LogInformation("📋 Création équipe Teams: Nom='{TeamName}', Description='{Description}', Enseignant={TeacherId}", 
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
                        result.GroupId = teamId; // Dans votre implémentation, c'est le même ID
                        
                        // 6. Enregistrer le mapping
                        var newMapping = new OUTeamsMapping
                        {
                            OUDistinguishedName = ouPath,
                            TeamId = teamId,
                            GroupId = teamId,
                            ClassName = ouName, // Ou extraire plus précisément si nécessaire
                            CreatedAt = DateTime.UtcNow,
                            LastSyncAt = DateTime.UtcNow,
                            IsActive = true
                        };
                        _teamsIntegrationConfig.Mappings.Add(newMapping);
                        await _configService.UpdateTeamsIntegrationSettingsAsync(_teamsIntegrationConfig); // Sauvegarder la config
                        
                        // 7. Synchroniser les utilisateurs existants de l'OU
                        await SyncExistingUsersAsync(ouPath, teamId, result);
                        
                        _logger.LogInformation("✅ Équipe Teams créée avec succès pour OU '{OUName}': {TeamId}", ouName, teamId);
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
        /// Ajoute un utilisateur à l'équipe Teams correspondant à son OU
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
                if (!_teamsIntegrationConfig.AutoAddUsersToTeams)
                {
                    _logger.LogDebug("⏭️ Ajout automatique utilisateurs Teams désactivé pour {User}", samAccountName);
                    return false;
                }

                // 1. Récupérer le mapping OU → Teams
                var mapping = _teamsIntegrationConfig.Mappings.FirstOrDefault(m => m.OUDistinguishedName.Equals(ouDn, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                {
                    _logger.LogDebug("ℹ️ Aucune équipe Teams trouvée pour OU '{OUDN}' (utilisateur: {User})", ouDn, samAccountName);
                    return false;
                }

                // 2. Récupérer l'utilisateur depuis AD
                var user = await _ldapService.GetUserAsync(samAccountName);
                if (user == null)
                {
                    _logger.LogWarning("⚠️ Utilisateur '{User}' non trouvé dans AD pour ajout à Teams", samAccountName);
                    return false;
                }

                // 3. Ajouter l'utilisateur à l'équipe Teams
                var userIds = new List<string> { GetUserIdFromUser(user) };
                var success = await _teamsService!.AddMembersToTeamAsync(mapping.GroupId, userIds);

                if (success)
                {
                    _logger.LogInformation("✅ Utilisateur '{User}' ajouté à l'équipe Teams '{TeamId}'", samAccountName, mapping.TeamId);
                    
                    // Mettre à jour les stats du mapping (directement dans la config)
                    mapping.MemberCount = (mapping.MemberCount + userIds.Count); // Simple incrémentation, à affiner si nécessaire
                    mapping.LastSyncAt = DateTime.UtcNow;
                    await _configService.UpdateTeamsIntegrationSettingsAsync(_teamsIntegrationConfig); // Sauvegarder la config
                }
                else
                {
                    _logger.LogWarning("⚠️ Échec ajout utilisateur '{User}' à l'équipe Teams '{TeamId}'", samAccountName, mapping.TeamId);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de l'ajout utilisateur '{User}' à Teams (OU: {OUDN})", samAccountName, ouDn);
                return false;
            }
        }

        /// <summary>
        /// Synchronise tous les utilisateurs d'une OU vers l'équipe Teams correspondante
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

                // 1. Récupérer le mapping
                var mapping = _teamsIntegrationConfig.Mappings.FirstOrDefault(m => m.OUDistinguishedName.Equals(ouDn, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                {
                    _logger.LogWarning("⚠️ Aucune équipe Teams trouvée pour OU '{OUDN}'", ouDn);
                    return false;
                }

                // 2. Récupérer tous les utilisateurs de l'OU
                var adUsers = await _ldapService.GetAllUsersInOuAsync(ouDn);
                if (!adUsers.Any())
                {
                    _logger.LogInformation("ℹ️ Aucun utilisateur trouvé dans OU '{OUDN}'", ouDn);
                    return true;
                }

                // 3. Récupérer les membres actuels de l'équipe Teams
                var currentTeamMembers = await _teamsService!.GetTeamMembersAsync(mapping.TeamId);

                // 4. Identifier les utilisateurs à ajouter
                var usersToAdd = new List<string>();
                foreach (var user in adUsers)
                {
                    var userId = GetUserIdFromUser(user);
                    if (!string.IsNullOrEmpty(userId) && !currentTeamMembers.Contains(userId))
                    {
                        usersToAdd.Add(userId);
                    }
                }

                // 5. Ajouter les utilisateurs manquants
                if (usersToAdd.Any())
                {
                    var success = await _teamsService.AddMembersToTeamAsync(mapping.GroupId, usersToAdd);
                    if (success)
                    {
                        _logger.LogInformation("✅ {Count} utilisateurs ajoutés à l'équipe Teams '{TeamId}'", usersToAdd.Count, mapping.TeamId);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ Échec ajout de {Count} utilisateurs à l'équipe Teams '{TeamId}'", usersToAdd.Count, mapping.TeamId);
                        return false;
                    }
                }
                else
                {
                    _logger.LogInformation("ℹ️ Tous les utilisateurs sont déjà dans l'équipe Teams '{TeamId}'", mapping.TeamId);
                }

                // 6. Mettre à jour les stats
                mapping.MemberCount = adUsers.Count; // Mettre à jour avec le compte total actuel
                mapping.LastSyncAt = DateTime.UtcNow;
                await _configService.UpdateTeamsIntegrationSettingsAsync(_teamsIntegrationConfig); // Sauvegarder la config

                _logger.LogInformation("✅ Synchronisation terminée pour OU '{OUDN}': {Count} utilisateurs", ouDn, adUsers.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la synchronisation OU '{OUDN}' vers Teams", ouDn);
                return false;
            }
        }

        /// <summary>
        /// Migre toutes les OUs existantes vers Teams
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

                // 1. Récupérer toutes les OUs
                var allOUs = await _ldapService.GetAllOrganizationalUnitsAsync();
                _logger.LogInformation("📊 {Count} OUs trouvées pour migration", allOUs.Count);

                // 2. Filtrer les OUs déjà migrées et exclues
                var ousToMigrate = new List<OrganizationalUnitModel>();
                foreach (var ou in allOUs)
                {
                    if (IsOUExcluded(ou.DistinguishedName))
                    {
                        _logger.LogDebug("⏭️ OU exclue: {OUDN}", ou.DistinguishedName);
                        continue;
                    }

                    var hasMapping = _teamsIntegrationConfig.Mappings.Any(m => m.OUDistinguishedName.Equals(ou.DistinguishedName, StringComparison.OrdinalIgnoreCase));
                    if (!hasMapping)
                    {
                        ousToMigrate.Add(ou);
                    }
                }

                _logger.LogInformation("📋 {Count} OUs à migrer vers Teams", ousToMigrate.Count);

                // 3. Migrer chaque OU avec limitation de débit
                var semaphore = new SemaphoreSlim(2, 2); // 2 migrations simultanées max
                var tasks = ousToMigrate.Select(async ou =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var ouName = ExtractOUName(ou.DistinguishedName);
                        var result = await CreateTeamFromOUAsync(ouName, ou.DistinguishedName);
                        
                        // Attendre un peu entre les créations pour éviter le throttling
                        await Task.Delay(2000);
                        
                        return result;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                results = (await Task.WhenAll(tasks)).ToList();

                // 4. Statistiques de migration
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
        /// Vérifie la santé de l'intégration Teams
        /// </summary>
        public async Task<TeamsIntegrationHealthStatus> GetHealthStatusAsync()
        {
            var status = new TeamsIntegrationHealthStatus
            {
                Enabled = _isEnabled
            };
            
            try
            {
                // Vérifier LDAP
                status.LdapConnected = _ldapService.IsLdapHealthy();
                
                // Vérifier Graph API si activé
                if (_isEnabled && _teamsService != null)
                {
                    try
                    {
                        // Test simple avec vérification d'existence d'une équipe fictive
                        await _teamsService.TeamExistsAsync("HealthCheckTeam");
                        status.GraphApiConnected = true;
                    }
                    catch
                    {
                        status.GraphApiConnected = false;
                    }
                }
                else
                {
                    status.GraphApiConnected = false;
                }

                // Compter les mappings actifs si le service est activé
                if (_isEnabled)
                {
                    status.ActiveMappingsCount = _teamsIntegrationConfig.Mappings.Count(m => m.IsActive);
                    status.Metrics["TotalMappings"] = status.ActiveMappingsCount;
                }

                // Déterminer le statut global
                if (!_isEnabled)
                {
                    status.IsHealthy = true; // Considéré comme sain si volontairement désactivé
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
        /// Resynchronise manuellement une OU spécifique
        /// </summary>
        public async Task<TeamsCreationResult> ResyncOUToTeamAsync(string ouDn)
        {
            if (!EnsureEnabled())
            {
                return new TeamsCreationResult
                {
                    Success = false,
                    OUPath = ouDn,
                    ErrorMessage = "Intégration Teams désactivée"
                };
            }

            _logger.LogInformation("🔄 Début resynchronisation manuelle OU '{OUDN}'", ouDn);
            
            try
            {
                // Synchroniser les utilisateurs
                var syncSuccess = await SyncOUUsersToTeamAsync(ouDn);
                
                var result = new TeamsCreationResult
                {
                    Success = syncSuccess,
                    OUPath = ouDn,
                    ClassName = ExtractOUName(ouDn)
                };
                
                if (!syncSuccess)
                {
                    result.ErrorMessage = "Échec de la synchronisation des utilisateurs";
                }
                
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

        #region Méthodes privées utilitaires

        private bool IsOUExcluded(string ouPath)
        {
            return _teamsIntegrationConfig.ExcludedOUs.Any(excluded => 
                ouPath.Contains(excluded, StringComparison.OrdinalIgnoreCase));
        }

        private string? ResolveTeacherId(string ouPath, string? providedTeacherId)
        {
            // 1. Utiliser l'enseignant fourni explicitement
            if (!string.IsNullOrEmpty(providedTeacherId))
                return providedTeacherId;

            // 2. Rechercher dans les mappings OU → Enseignant
            var mapping = _teamsIntegrationConfig.OUTeacherMappings.FirstOrDefault(kvp => 
                ouPath.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(mapping.Value))
                return mapping.Value;

            // 3. Utiliser l'enseignant par défaut
            return _teamsIntegrationConfig.DefaultTeacherUserId;
        }

        private string GenerateTeamName(string ouName)
        {
            return _teamsIntegrationConfig.TeamNamingTemplate.Replace("{OUName}", ouName);
        }

        private string GenerateTeamDescription(string ouName)
        {
            return _teamsIntegrationConfig.TeamDescriptionTemplate.Replace("{OUName}", ouName);
        }

        private string GenerateMailNickname(string ouName)
        {
            // Nettoyer le nom pour créer un nickname valide
            var nickname = Regex.Replace(ouName.ToLower(), @"[^a-z0-9]", "");
            return nickname.Length > 20 ? nickname.Substring(0, 20) : nickname;
        }

        private string ExtractOUName(string ouDn)
        {
            // Extraire le nom de l'OU depuis le DN
            // Ex: "OU=ClasseMaths,OU=Classes,DC=domain,DC=com" → "ClasseMaths"
            var match = Regex.Match(ouDn, @"^OU=([^,]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : ouDn;
        }

        private string GetUserIdFromUser(UserModel user)
        {
            // Convertir l'utilisateur AD en ID utilisable par Graph API
            return user.UserPrincipalName ?? user.SamAccountName;
        }

        private async Task<string?> CreateTeamWithRetryAsync(string teamName, string description, string mailNickname, string teacherId)
        {
            var attemptCount = 0;
            var maxAttempts = _teamsIntegrationConfig.MaxRetryAttempts;

            while (attemptCount < maxAttempts)
            {
                try
                {
                    attemptCount++;
                    _logger.LogDebug("🔄 Tentative {Attempt}/{Max} création équipe '{TeamName}'", attemptCount, maxAttempts, teamName);

                    var teamId = await _teamsService!.CreateClassTeamAsync(teamName, description, mailNickname, teacherId);
                    
                    if (!string.IsNullOrEmpty(teamId))
                    {
                        _logger.LogInformation("✅ Équipe créée avec succès: '{TeamName}' (tentative {Attempt})", teamName, attemptCount);
                        return teamId;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Échec tentative {Attempt}/{Max} création équipe '{TeamName}': {Error}", 
                        attemptCount, maxAttempts, teamName, ex.Message);

                    if (attemptCount < maxAttempts)
                    {
                        var delay = _teamsIntegrationConfig.RetryDelayMs * attemptCount; // Backoff exponentiel
                        _logger.LogDebug("⏳ Attente {Delay}ms avant nouvelle tentative", delay);
                        await Task.Delay(delay);
                    }
                }
            }

            _logger.LogError("❌ Échec définitif création équipe '{TeamName}' après {Attempts} tentatives", teamName, maxAttempts);
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
                            _logger.LogInformation("✅ {Count} utilisateurs existants ajoutés à l'équipe {TeamId}", userIds.Count, teamId);
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
                _logger.LogWarning(ex, "⚠️ Erreur lors de la synchronisation des utilisateurs existants pour équipe {TeamId}", teamId);
            }
        }

        #endregion

        public void Dispose()
        {
            _creationSemaphore?.Dispose();
        }
    }
}