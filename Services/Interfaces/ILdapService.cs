using ADManagerAPI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ADManagerAPI.Services.Interfaces
{
    public interface ILdapService : IDisposable
    {
        Task<bool> OrganizationalUnitExistsAsync(string ouPath);
        Task<bool> UserExistsAsync(string samAccountName);
        void CreateOrganizationalUnit(string ouPath);
        UserModel CreateUser(string samAccountName, Dictionary<string, string> attributes, string ou);
        void UpdateUser(string samAccountName, Dictionary<string, string> attributes);
        Task<List<OrganizationalUnitModel>> GetAllOrganizationalUnitsAsync();
        Task<bool> TestConnection();
        Task<List<string>> GetUsersInOUAsync(string ouDistinguishedName);
        void DeleteUser(string samAccountName, string currentOuDn);
        Task DeleteUserAsync(string samAccountName, string currentOuDn);
        Task DeleteOrganizationalUnitAsync(string ouDn);
        Task<List<string>> GetOrganizationalUnitPathsRecursiveAsync(string baseOuDn);
        Task<bool> IsOrganizationalUnitEmptyAsync(string ouDn);
        void DeleteOrganizationalUnit(string ouDn);
    }
}