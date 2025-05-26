using System.Text.Json;
using System.Text.Json.Serialization;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace ADManagerAPI.Services;

public class ConfigService : IConfigService
{
    private readonly ILogger<ConfigService> _logger;
    private readonly string _configPath;
    private readonly string _settingsFilePath;
    private ApplicationSettings _settings;

    public ConfigService(ILogger<ConfigService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
        _settingsFilePath = Path.Combine(_configPath, "settings.json");
        Directory.CreateDirectory(_configPath);
        LoadSettingsAsync().Wait();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath);
                _settings = JsonSerializer.Deserialize<ApplicationSettings>(json) ?? new ApplicationSettings();
                _settings.FolderManagement ??= new FolderManagementSettings();
                _settings.Fsrm ??= new FsrmSettings();
            }
            else
            {
                _settings = new ApplicationSettings();
                _settings.FolderManagement ??= new FolderManagementSettings();
                _settings.Fsrm ??= new FsrmSettings();
                await SaveSettingsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors du chargement des paramètres");
            _settings = new ApplicationSettings();
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            
            var json = JsonSerializer.Serialize(_settings, options);
            await File.WriteAllTextAsync(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la sauvegarde des paramètres");
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

    #region Configurations d'import
    public Task<List<SavedImportConfig>> GetSavedImportConfigs()
    {
        try
        {
            return Task.FromResult(_settings.Imports);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des configurations d'import");
            throw;
        }
    }
    
    public async Task<SavedImportConfig> SaveImportConfig(SavedImportConfig config)
    {
        try
        {
            if (string.IsNullOrEmpty(config.Id))
            {
                config.Id = Guid.NewGuid().ToString();
            }

            var existingConfig = _settings.Imports.FirstOrDefault(c => c.Id == config.Id);
            if (existingConfig != null)
            {
                _settings.Imports.Remove(existingConfig);
            }
            
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
    /// Supprime toutes les configurations sauvegardées
    /// </summary>
    public Task DeleteAllImportConfigs()
    {
        try
        {
            var configDir = GetConfigDirectory();
            var files = Directory.GetFiles(configDir, "*.import.json");
            foreach (var file in files)
            {
                File.Delete(file);
            }
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la suppression de toutes les configurations d'import");
            throw;
        }
    }

    /// <summary>
    /// Supprime une configuration sauvegardée
    /// </summary>
    /// <param name="id">ID de la configuration à supprimer</param>
    private string GetConfigPath(string id)
    {
        return Path.Combine(GetConfigDirectory(), $"{id}.import.json");
    }

    private string GetConfigDirectory()
    {
        return _configPath;
    }

    /// <summary>
    /// Génère une configuration d'import par défaut
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
            {
                config.HeaderMapping = new Dictionary<string, string>
                {
                    { "login", "sAMAccountName" },
                    { "firstname", "givenName" },
                    { "lastname", "sn" },
                    { "email", "mail" },
                    { "phone", "telephoneNumber" },
                    { "title", "title" }
                };
            }
            else if (objectType.Equals("computer", StringComparison.OrdinalIgnoreCase))
            {
                config.HeaderMapping = new Dictionary<string, string>
                {
                    { "name", "sAMAccountName" },
                    { "description", "description" },
                    { "location", "location" }
                };
            }
            
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

    public async Task UpdateLdapSettingsAsync(LdapSettings config)
    {
        try
        {
            _settings.Ldap = config;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration LDAP");
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
                _logger.LogInformation("Aucun attribut trouvé dans la configuration, utilisation de la configuration par défaut");
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
            new AdAttributeDefinition { Name = "objectClass", Description = "Type d'objet dans l'Active Directory", Syntax = "StringArray", IsRequired = true },
            new AdAttributeDefinition { Name = "title", Description = "Titre ou fonction", Syntax = "String", IsRequired = false },
            new AdAttributeDefinition { Name = "samAccountName", Description = "Nom de connexion de l'utilisateur", Syntax = "String", IsRequired = true },
            new AdAttributeDefinition { Name = "userPrincipalName", Description = "Nom principal de l'utilisateur", Syntax = "String", IsRequired = true },
            new AdAttributeDefinition { Name = "mail", Description = "Adresse email", Syntax = "String", IsRequired = false },
            new AdAttributeDefinition { Name = "givenName", Description = "Prénom de l'utilisateur", Syntax = "String", IsRequired = true },
            new AdAttributeDefinition { Name = "sn", Description = "Nom de famille", Syntax = "String", IsRequired = true },
            new AdAttributeDefinition { Name = "initials", Description = "Initiales de l'utilisateur", Syntax = "String", IsRequired = false },
            new AdAttributeDefinition { Name = "cn", Description = "Nom commun complet", Syntax = "String", IsRequired = true }
        };
    }
    #endregion

    #region Folder Management Settings
    public Task<FolderManagementSettings> GetFolderManagementSettingsAsync()
    {
        try
        {
            _settings ??= new ApplicationSettings();
            _settings.FolderManagement ??= new FolderManagementSettings();
            return Task.FromResult(_settings.FolderManagement);
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
            _settings.FolderManagement = settings;
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
            _settings.Fsrm ??= new FsrmSettings();
            return Task.FromResult(_settings.Fsrm);
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
            _settings.Fsrm = settings;
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration FSRM");
            throw;
        }
    }
    #endregion
}