using ADManagerAPI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ADManagerAPI.Services.Interfaces
{
    public interface IFolderManagementService
    {
        Task<bool> CreateStudentFolderAsync(StudentInfo student, string templateName, UserRole role);
        Task<bool> CreateClassGroupFolderAsync(ClassGroupInfo classGroup, string templateName);
        Task<bool> ProvisionUserShareAsync(string serverName, string localPhysicalBasePathOnServer, string configuredShareName, string accountAd, List<string> subfolders);
    }
} 