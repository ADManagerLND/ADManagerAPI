using ADManagerAPI.Controllers;
using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces;

public interface ILdapService : IDisposable
{
    Task<bool> OrganizationalUnitExistsAsync(string ouPath);
    Task<bool> UserExistsAsync(string samAccountName);
    void CreateOrganizationalUnit(string ouPath);
    Task<UserModel> CreateUser(string samAccountName, Dictionary<string, string> attributes, string ou);
    void UpdateUser(string samAccountName, Dictionary<string, string> attributes);
    Task<List<OrganizationalUnitModel>> GetAllOrganizationalUnitsAsync();
    Task<bool> TestConnectionAsync();
    Task<List<string>> GetUsersInOUAsync(string ouDn);
    void DeleteUser(string samAccountName, string currentOuDn);
    Task DeleteUserAsync(string samAccountName, string currentOuDn);
    Task<bool> DeleteOrganizationalUnitAsync(string ouDn, bool deleteIfNotEmpty = false);
    Task<List<string>> GetOrganizationalUnitPathsRecursiveAsync(string baseOuDn);
    Task<bool> IsOrganizationalUnitEmptyAsync(string ouDn);
    Task<bool> IsOrganizationalUnitEmptyOfUsersAsync(string ouDn);
    void DeleteOrganizationalUnit(string ouDn);
    bool IsLdapHealthy();
    LdapHealthStatus GetHealthStatus();
    Task CreateOrganizationalUnitAsync(string ouPath);
    Task<UserModel?> GetUserAsync(string samAccountName);
    Task<List<UserModel>> GetAllUsersInOuAsync(string ouPath);
    Task CreateUserAsync(Dictionary<string, string> attributes, string ouPath, string? defaultPassword = null);
    Task UpdateUserAsync(string samAccountName, Dictionary<string, string> attributes, string ouPath);
    Task<Dictionary<string, string?>> GetUserAttributesAsync(string samAccountName, List<string> attributeNames);

    Task<bool> CompareAndUpdateUserAsync(string samAccountName, Dictionary<string, string> newAttributes,
        string ouPath);

    void CreateGroup(string groupName, string ouDn, bool isSecurity = true, bool isGlobal = true,
        string? description = null, string? parentGroupDn = null);

    void AddUserToGroup(string userDn, string groupDn);

    void AddGroupToGroup(string childGroupDn, string parentGroupDn);

    #region Nouvelles méthodes batch pour optimisation

    Task<List<UserModel>> GetUsersBatchAsync(List<string> samAccountNames);

    Task<HashSet<string>> GetOrganizationalUnitsBatchAsync(List<string> ouPaths);

    Task<List<UserModel>> SearchUsersAsync(string searchBase, string ldapFilter);

    Task<Dictionary<string, Dictionary<string, string>>> GetUsersAttributesBatchAsync(
        List<string> userIdentifiers,
        List<string> attributeNames);
    
    Task<List<string>> GetAllSamAccountNamesInOuBatchAsync(string ouPath);

    #endregion

    #region Méthodes de déplacement d'utilisateurs

    Task MoveUserAsync(string samAccountName, string sourceOu, string targetOu);

    Task<string?> GetUserCurrentOuAsync(string samAccountName);

    Task<List<UserModel>> GetUsersAsync(string parentDn, int maxResults = 50);
    Task<List<OrganizationalUnitModel>> GetOrganizationalUnitsAsync(string parentDn);
    Task<List<LdapService.ContainerModel>> GetContainersAsync(string parentDn);

    Task DoBulkActionAsync(string userDn, ActiveDirectoryController.BulkActionRequestDto request);

    #endregion

    #region Méthodes de gestion des groupes vides

    /// <summary>
    /// Vérifie si un groupe est vide (sans membres)
    /// </summary>
    Task<bool> IsGroupEmptyAsync(string groupDn);
    
    /// <summary>
    /// Supprime un groupe de sécurité ou de distribution
    /// </summary>
    Task DeleteGroupAsync(string groupDn);
    
    /// <summary>
    /// Récupère tous les groupes dans une OU donnée
    /// </summary>
    Task<List<string>> GetGroupsInOUAsync(string ouDn);

    #endregion
}