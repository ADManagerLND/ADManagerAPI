using ADManagerAPI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ADManagerAPI.Services.Interfaces;

public interface IConfigService
{
    #region Configurations d'import
    Task<List<SavedImportConfig>> GetSavedImportConfigs();
    Task<SavedImportConfig> SaveImportConfig(SavedImportConfig config);
    Task<bool> DeleteImportConfig(string configId);
    Task DeleteAllImportConfigs();
    Task<ImportConfig> GenerateDefaultImportConfig(string objectType);
    #endregion

    #region Configuration générale
    Task<ApiSettings> GetApiSettingsAsync();
    Task UpdateApiSettingsAsync(ApiSettings config);
    #endregion

    #region Configuration LDAP
    Task<LdapSettings> GetLdapSettingsAsync();
    Task UpdateLdapSettingsAsync(LdapSettings config);
    #endregion

    #region Attributs utilisateur
    Task<List<AdAttributeDefinition>> GetUserAttributesAsync();
    Task UpdateUserAttributesAsync(List<AdAttributeDefinition> attributes);
    #endregion

    #region Folder Management Settings
    Task<FolderManagementSettings> GetFolderManagementSettingsAsync();
    Task UpdateFolderManagementSettingsAsync(FolderManagementSettings settings);
    #endregion

    #region FSRM Settings
    Task<FsrmSettings> GetFsrmSettingsAsync();
    Task UpdateFsrmSettingsAsync(FsrmSettings settings);
    #endregion

    #region Configuration complète
    Task<ApplicationSettings> GetAllSettingsAsync();
    Task UpdateAllSettingsAsync(ApplicationSettings settings);
    #endregion
}