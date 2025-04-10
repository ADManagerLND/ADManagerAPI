using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces;

public interface IConfigService
{
    #region Configuration d'import
    Task<List<SavedImportConfig>> GetSavedImportConfigs();
    Task<SavedImportConfig> SaveImportConfig(SavedImportConfig config);
    Task<bool> DeleteImportConfig(string configId);
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

    #region Configuration complète
    Task<ApplicationSettings> GetAllSettingsAsync();
    Task UpdateAllSettingsAsync(ApplicationSettings settings);
    #endregion
}