using System.Text.Json;
using System.Text.Json.Serialization;
using ADManagerAPI.Config;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace ADManagerAPI.Services;

public class ConfigService : IConfigService
{
    private readonly string _configPath;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly ILogger<ConfigService> _logger;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly string _settingsFilePath;
    private ApplicationSettings _settings;

    public ConfigService(ILogger<ConfigService> logger, IDataProtectionProvider dataProtectionProvider)
    {
        _logger = logger;

        // Correction du chemin pour utiliser le dossier Config à la racine de l'application
        var basePath =
            Directory.GetCurrentDirectory(); // Chemin racine de l'application au lieu de AppDomain.CurrentDomain.BaseDirectory qui pointe vers bin/
        _configPath = Path.Combine(basePath, "Config");
        _settingsFilePath = Path.Combine(_configPath, "settings.json");

        Directory.CreateDirectory(_configPath);
        _dataProtectionProvider = dataProtectionProvider;
        _serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null // Utiliser les annotations JsonPropertyName explicites
        };
        LoadSettingsAsync().Wait();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _logger.LogInformation("Chargement des paramètres depuis : {FilePath}", _settingsFilePath);

            if (File.Exists(_settingsFilePath))
            {
                _logger.LogInformation("Fichier de configuration trouvé, chargement des paramètres");
                var json = await File.ReadAllTextAsync(_settingsFilePath);
                _settings = JsonSerializer.Deserialize<ApplicationSettings>(json) ?? new ApplicationSettings();
                _settings.FolderManagementSettings ??= new FolderManagementSettings();
                _settings.FsrmSettings ??= new FsrmSettings();
                _logger.LogInformation("Paramètres chargés avec succès");
            }
            else
            {
                _logger.LogWarning(
                    "Fichier de configuration non trouvé à {FilePath}, création d'une nouvelle configuration",
                    _settingsFilePath);
                _settings = new ApplicationSettings();
                _settings.FolderManagementSettings ??= new FolderManagementSettings();
                _settings.FsrmSettings ??= new FsrmSettings();
                await SaveSettingsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors du chargement des paramètres depuis {FilePath}", _settingsFilePath);
            _settings = new ApplicationSettings();
        }
    }

    private async Task SaveSettingsAsync(ApplicationSettings? settings = null)
    {
        try
        {
            _logger.LogInformation("Début de sauvegarde des paramètres dans {FilePath}", _settingsFilePath);

            settings ??= _settings;
            var json = JsonSerializer.Serialize(settings, _serializerOptions);

            // S'assurer que le répertoire existe
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!Directory.Exists(directory) && directory != null)
            {
                _logger.LogInformation("Création du répertoire {Directory}", directory);
                Directory.CreateDirectory(directory);
            }

            // Écrire directement dans un nouveau fichier temporaire puis remplacer le fichier existant
            // pour éviter les problèmes de verrouillage ou d'écriture partielle
            var tempFile = _settingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tempFile, json);

            if (File.Exists(_settingsFilePath)) File.Delete(_settingsFilePath);

            File.Move(tempFile, _settingsFilePath);

            // Mettre à jour la version en mémoire
            _settings = settings;

            _logger.LogInformation("Paramètres sauvegardés avec succès dans {FilePath}", _settingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de l'enregistrement des paramètres dans {FilePath}", _settingsFilePath);
            throw;
        }
    }

    #region Configuration complète

    public async Task<ApplicationSettings> GetAllSettingsAsync()
    {
        return _settings;
    }

    public async Task UpdateAllSettingsAsync(ApplicationSettings settings)
    {
        _settings = settings;
        await SaveSettingsAsync();
    }

    #endregion

    #region Configurations d'import (utilisées aussi pour les mappages AD)

    public Task<List<SavedImportConfig>> GetSavedImportConfigs()
    {
        try
        {
            // Si aucune configuration n'existe, charger la configuration du Lycée par défaut
            if (_settings.Imports == null || _settings.Imports.Count == 0) LoadDefaultLyceeConfig();

            // CLONAGE PROFOND pour éviter la pollution de l'état du singleton
            var serialized = JsonSerializer.Serialize(_settings.Imports, _serializerOptions);
            var clonedConfigs = JsonSerializer.Deserialize<List<SavedImportConfig>>(serialized) 
                                ?? new List<SavedImportConfig>();

            return Task.FromResult(clonedConfigs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des configurations d'import");
            throw;
        }
    }

    private void LoadDefaultLyceeConfig()
    {
        try
        {
            var defaultConfigPath = Path.Combine(_configPath, "lycee-import-optimized-2025.json");

            if (File.Exists(defaultConfigPath))
            {
                _logger.LogInformation("Chargement de la configuration par défaut du Lycée Notre-Dame");
                var json = File.ReadAllText(defaultConfigPath);
                var defaultConfig = JsonSerializer.Deserialize<SavedImportConfig>(json, _serializerOptions);

                if (defaultConfig != null)
                {
                    _settings.Imports = new List<SavedImportConfig> { defaultConfig };
                    _logger.LogInformation("Configuration par défaut chargée avec succès");
                }
            }
            else
            {
                _logger.LogWarning("Fichier de configuration par défaut non trouvé: {Path}", defaultConfigPath);
                _settings.Imports = new List<SavedImportConfig>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors du chargement de la configuration par défaut");
            _settings.Imports = new List<SavedImportConfig>();
        }
    }

    public async Task<SavedImportConfig> SaveImportConfig(SavedImportConfig config)
    {
        try
        {
            if (string.IsNullOrEmpty(config.Id)) config.Id = Guid.NewGuid().ToString();

            var existingConfig = _settings.Imports.FirstOrDefault(c => c.Id == config.Id);
            if (existingConfig != null) _settings.Imports.Remove(existingConfig);

            _settings.Imports.Add(config);
            await SaveSettingsAsync();

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la sauvegarde de la configuration d'import");
            throw;
        }
    }

    public async Task<bool> DeleteImportConfig(string configId)
    {
        try
        {
            var config = _settings.Imports.FirstOrDefault(c => c.Id == configId);
            if (config != null)
            {
                _settings.Imports.Remove(config);
                await SaveSettingsAsync();
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la suppression de la configuration d'import");
            throw;
        }
    }

    /// <summary>
    ///     Supprime toutes les configurations sauvegardées
    /// </summary>
    public Task DeleteAllImportConfigs()
    {
        try
        {
            var configDir = GetConfigDirectory();
            var files = Directory.GetFiles(configDir, "*.import.json");
            foreach (var file in files) File.Delete(file);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la suppression de toutes les configurations d'import");
            throw;
        }
    }

    /// <summary>
    ///     Supprime une configuration sauvegardée
    /// </summary>
    /// <param name="id">ID de la configuration à supprimer</param>
    private string GetConfigPath(string id)
    {
        return Path.Combine(GetConfigDirectory(), $"{id}.import.json");
    }

    private string GetConfigDirectory()
    {
        // Utiliser le chemin racine de l'application + Config
        var basePath = Directory.GetCurrentDirectory();
        return Path.Combine(basePath, "Config");
    }

    /// <summary>
    ///     Génère une configuration d'import par défaut
    /// </summary>
    /// <param name="objectType">Type d'objet (utilisateur, ordinateur, etc.)</param>
    /// <returns>Configuration par défaut</returns>
    public Task<ImportConfig> GenerateDefaultImportConfig(string objectType)
    {
        try
        {
            var config = new ImportConfig
            {
                DefaultOU = "DC=domain,DC=local",
                CsvDelimiter = ';',
                HeaderMapping = new Dictionary<string, string>(),
                ManualColumns = new List<string>()
            };

            // Ajouter des mappages par défaut selon le type d'objet
            if (objectType.Equals("user", StringComparison.OrdinalIgnoreCase))
                config.HeaderMapping = new Dictionary<string, string>
                {
                    { "sAMAccountName", "%prenom:lowercase%.%nom:lowercase%" },
                    { "givenName", "%prenom%" },
                    { "sn", "%nom:uppercase%" },
                    { "mail", "%prenom:lowercase%.%nom:lowercase%@entreprise.com" },
                    { "userPrincipalName", "%prenom:lowercase%.%nom:lowercase%@entreprise.com" },
                    { "displayName", "%prenom% %nom:uppercase%" },
                    { "cn", "%prenom% %nom%" },
                    { "telephoneNumber", "%telephone%" },
                    { "title", "%fonction%" }
                };
            else if (objectType.Equals("computer", StringComparison.OrdinalIgnoreCase))
                config.HeaderMapping = new Dictionary<string, string>
                {
                    { "sAMAccountName", "%nom%" },
                    { "description", "%description%" },
                    { "location", "%emplacement%" }
                };

            return Task.FromResult(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erreur lors de la génération de la configuration par défaut pour {objectType}");
            throw;
        }
    }

    #endregion

    #region Configuration générale

    public Task<ApiSettings> GetApiSettingsAsync()
    {
        try
        {
            return Task.FromResult(_settings.Api);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration générale");
            throw;
        }
    }

    public async Task UpdateApiSettingsAsync(ApiSettings config)
    {
        try
        {
            _settings.Api = config;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration générale");
            throw;
        }
    }

    #endregion

    #region Configuration LDAP

    public async Task<LdapSettings> GetLdapSettingsAsync()
    {
        try
        {
            return _settings.Ldap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration LDAP");
            throw;
        }
    }

    public async Task UpdateLdapSettingsAsync(LdapSettings ldapSettings)
    {
        if (ldapSettings == null) throw new ArgumentNullException(nameof(ldapSettings));

        _logger.LogInformation("Début de mise à jour des paramètres LDAP");

        try
        {
            // Chiffrer le mot de passe avant de le stocker
            var encryptionHelper = new EncryptionHelper(_dataProtectionProvider);
            var encryptedPassword = encryptionHelper.EncryptString(ldapSettings.LdapPassword);
            _logger.LogInformation("Mot de passe chiffré avec succès");

            // Mettre à jour les paramètres LDAP dans la configuration mémoire
            _settings.Ldap.LdapServer = ldapSettings.LdapServer;
            _settings.Ldap.LdapDomain = ldapSettings.LdapDomain;
            _settings.Ldap.LdapPort = ldapSettings.LdapPort;
            _settings.Ldap.LdapBaseDn = ldapSettings.LdapBaseDn;
            _settings.Ldap.LdapUsername = ldapSettings.LdapUsername;
            _settings.Ldap.LdapPassword = encryptedPassword; // Stocke le mot de passe chiffré
            _settings.Ldap.LdapSsl = ldapSettings.LdapSsl;
            _settings.Ldap.LdapPageSize = ldapSettings.LdapPageSize;

            // Sauvegarder explicitement dans le fichier
            await SaveSettingsAsync(_settings);
            _logger.LogInformation("Paramètres LDAP sauvegardés dans le fichier {FilePath}", _settingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour des paramètres LDAP");
            throw;
        }
    }

    #endregion

    #region Attributs utilisateur

    public Task<List<AdAttributeDefinition>> GetUserAttributesAsync()
    {
        try
        {
            if (_settings.UserAttributes.Attributes == null || _settings.UserAttributes.Attributes.Count == 0)
            {
                _logger.LogInformation(
                    "Aucun attribut trouvé dans la configuration, utilisation de la configuration par défaut");
                return Task.FromResult(GetDefaultUserAttributes());
            }

            return Task.FromResult(_settings.UserAttributes.Attributes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des attributs utilisateur");
            throw;
        }
    }

    public async Task UpdateUserAttributesAsync(List<AdAttributeDefinition> attributes)
    {
        try
        {
            _settings.UserAttributes.Attributes = attributes;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour des attributs utilisateur");
            throw;
        }
    }

    private List<AdAttributeDefinition> GetDefaultUserAttributes()
    {
        return new List<AdAttributeDefinition>
        {
            new()
            {
                Name = "objectClass", Description = "Type d'objet dans l'Active Directory", Syntax = "StringArray",
                IsRequired = true
            },
            new() { Name = "title", Description = "Titre ou fonction", Syntax = "String", IsRequired = false },
            new()
            {
                Name = "sAMAccountName", Description = "Nom de connexion de l'utilisateur", Syntax = "String",
                IsRequired = true
            },
            new()
            {
                Name = "userPrincipalName", Description = "Nom principal de l'utilisateur", Syntax = "String",
                IsRequired = true
            },
            new() { Name = "mail", Description = "Adresse email", Syntax = "String", IsRequired = false },
            new() { Name = "givenName", Description = "Prénom de l'utilisateur", Syntax = "String", IsRequired = true },
            new() { Name = "sn", Description = "Nom de famille", Syntax = "String", IsRequired = true },
            new()
            {
                Name = "initials", Description = "Initiales de l'utilisateur", Syntax = "String", IsRequired = false
            },
            new() { Name = "cn", Description = "Nom commun complet", Syntax = "String", IsRequired = true }
        };
    }

    #endregion

    #region Folder Management Settings

    public Task<FolderManagementSettings> GetFolderManagementSettingsAsync()
    {
        try
        {
            _settings ??= new ApplicationSettings();
            _settings.FolderManagementSettings ??= new FolderManagementSettings();
            return Task.FromResult(_settings.FolderManagementSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration de gestion des dossiers");
            throw;
        }
    }

    public async Task UpdateFolderManagementSettingsAsync(FolderManagementSettings settings)
    {
        try
        {
            _settings.FolderManagementSettings = settings;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration de gestion des dossiers");
            throw;
        }
    }

    #endregion

    #region FSRM Settings

    public Task<FsrmSettings> GetFsrmSettingsAsync()
    {
        try
        {
            _settings ??= new ApplicationSettings();
            _settings.FsrmSettings ??= new FsrmSettings();
            return Task.FromResult(_settings.FsrmSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration FSRM");
            throw;
        }
    }

    public async Task UpdateFsrmSettingsAsync(FsrmSettings settings)
    {
        try
        {
            _settings.FsrmSettings = settings;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration FSRM");
            throw;
        }
    }

    #endregion

    #region Teams Integration Settings

    public async Task<TeamsIntegrationConfig> GetTeamsIntegrationSettingsAsync()
    {
        try
        {
            _settings ??= new ApplicationSettings();
            _settings.TeamsIntegration ??= new TeamsIntegrationConfig();
            return _settings.TeamsIntegration;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration d'intégration Teams");
            throw;
        }
    }

    public async Task UpdateTeamsIntegrationSettingsAsync(TeamsIntegrationConfig settings)
    {
        try
        {
            _settings.TeamsIntegration = settings;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration d'intégration Teams");
            throw;
        }
    }

    #endregion

    #region Azure Configuration Settings

    public async Task<AzureADConfig> GetAzureADConfigAsync()
    {
        try
        {
            return _settings.AzureAD ?? new AzureADConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration Azure AD");
            return new AzureADConfig();
        }
    }

    public async Task UpdateAzureADConfigAsync(AzureADConfig config)
    {
        try
        {
            _settings.AzureAD = config;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration Azure AD");
            throw;
        }
    }

    public async Task<GraphApiConfig> GetGraphApiConfigAsync()
    {
        try
        {
            return _settings.GraphApi;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration Graph API");
            return new GraphApiConfig();
        }
    }

    public async Task UpdateGraphApiConfigAsync(GraphApiConfig config)
    {
        try
        {
            _settings.GraphApi = config;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration Graph API");
            throw;
        }
    }

    #endregion
}