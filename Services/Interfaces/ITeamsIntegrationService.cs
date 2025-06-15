using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces
{
    /// <summary>
    /// Interface principale pour l'intégration Teams avec les OUs Active Directory
    /// </summary>
    public interface ITeamsIntegrationService
    {
        /// <summary>
        /// Crée une équipe Teams à partir d'une OU nouvellement créée
        /// </summary>
        Task<TeamsCreationResult> CreateTeamFromOUAsync(string ouName, string ouPath, string? teacherId = null);
        
        /// <summary>
        /// Ajoute un utilisateur à l'équipe Teams correspondant à son OU
        /// </summary>
        Task<bool> AddUserToOUTeamAsync(string samAccountName, string ouDn);
        
        /// <summary>
        /// Synchronise tous les utilisateurs d'une OU vers l'équipe Teams correspondante
        /// </summary>
        Task<bool> SyncOUUsersToTeamAsync(string ouDn);
        
        /// <summary>
        /// Migre toutes les OUs existantes vers Teams (une seule fois)
        /// </summary>
        Task<List<TeamsCreationResult>> MigrateExistingOUsAsync();
        
        /// <summary>
        /// Vérifie la santé de l'intégration Teams
        /// </summary>
        Task<TeamsIntegrationHealthStatus> GetHealthStatusAsync();
        
        /// <summary>
        /// Resynchronise manuellement une OU spécifique
        /// </summary>
        Task<TeamsCreationResult> ResyncOUToTeamAsync(string ouDn);
    }

    /// <summary>
    /// Interface pour le service Teams (votre service existant adapté)
    /// </summary>
    public interface ITeamsGroupService
    {
        Task<string> CreateClassTeamAsync(string className, string classDescription, string classMailNickname, string teacherUserId);
        Task<bool> AddMembersToTeamAsync(string groupId, List<string> userIds);
        Task<string> GetChannelIdByNameAsync(string teamId, string channelName);
        Task CreateFolderInChannelFilesAsync(string teamId, string channelId, string folderName);
        Task SetReadOnlyPermissionsForVisitorGroupAsync(string teamId, string channelId, string folderName, string visitorGroupName, string permission);
        Task<bool> TeamExistsAsync(string teamName);
        Task<List<string>> GetTeamMembersAsync(string teamId);
    }

    /// <summary>
    /// Interface pour le mapping entre OUs et Teams
    /// </summary>
    public interface IOUTeamsMapperService
    {
        /// <summary>
        /// Enregistre un mapping OU → Teams
        /// </summary>
        Task SaveMappingAsync(string ouDn, string teamId, string groupId);
        
        /// <summary>
        /// Récupère le mapping Teams pour une OU
        /// </summary>
        Task<OUTeamsMapping?> GetMappingAsync(string ouDn);
        
        /// <summary>
        /// Vérifie si une OU a déjà une équipe Teams associée
        /// </summary>
        Task<bool> HasTeamMappingAsync(string ouDn);
        
        /// <summary>
        /// Supprime un mapping (si OU ou Team supprimée)
        /// </summary>
        Task RemoveMappingAsync(string ouDn);
        
        /// <summary>
        /// Récupère tous les mappings actifs
        /// </summary>
        Task<List<OUTeamsMapping>> GetAllMappingsAsync();
        
        /// <summary>
        /// Met à jour les statistiques d'un mapping
        /// </summary>
        Task UpdateMappingStatsAsync(string ouDn, int memberCount);
    }
}