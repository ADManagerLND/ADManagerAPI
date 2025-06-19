namespace ADManagerAPI.Services.Interfaces;

public interface IFolderManagementService
{
    Task<bool> CheckUserShareExistsAsync(string? foldersTargetServerName, string cleanedSamAccountName,
        string? foldersLocalPathForUserShareOnServer);

    Task<bool> ProvisionUserShareAsync(string argServerName, string argLocalPath, string argShareName,
        string argAccountAd, List<string> argSubfolders);
}