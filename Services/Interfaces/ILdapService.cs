using ADManagerAPI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

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
        Task<bool> TestConnectionAsync();
        Task<List<string>> GetUsersInOUAsync(string ouDistinguishedName);
        void DeleteUser(string samAccountName, string currentOuDn);
        Task DeleteUserAsync(string samAccountName, string currentOuDn);
        Task<bool> DeleteOrganizationalUnitAsync(string ouDn, bool deleteIfNotEmpty = false);
        Task<List<string>> GetOrganizationalUnitPathsRecursiveAsync(string baseOuDn);
        Task<bool> IsOrganizationalUnitEmptyAsync(string ouDn);
        void DeleteOrganizationalUnit(string ouDn);
    }
}