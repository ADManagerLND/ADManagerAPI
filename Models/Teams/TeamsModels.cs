using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ADManagerAPI.Models;

/// <summary>
///     Configuration Teams pour un import spécifique
/// </summary>
public class TeamsImportConfig
{
    /// <summary>
    ///     Active l'intégration Teams pour cet import
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;



    /// <summary>
    ///     Active l'ajout automatique des utilisateurs aux équipes Teams
    /// </summary>
    [JsonPropertyName("autoAddUsersToTeams")]
    public bool AutoAddUsersToTeams { get; set; } = false;

    /// <summary>
    ///     ID de l'enseignant par défaut à utiliser si aucun n'est spécifié
    /// </summary>
    [JsonPropertyName("defaultTeacherUserId")]
    public string? DefaultTeacherUserId { get; set; }

    /// <summary>
    ///     Template pour le nom des équipes Teams. Utilisez {OUName} comme placeholder
    /// </summary>
    [JsonPropertyName("teamNamingTemplate")]
    public string TeamNamingTemplate { get; set; } = "Classe {OUName}";

    /// <summary>
    ///     Template pour la description des équipes. Utilisez {OUName} comme placeholder
    /// </summary>
    [JsonPropertyName("teamDescriptionTemplate")]
    public string TeamDescriptionTemplate { get; set; } = "Équipe pour la classe {OUName}";

/**/ /*
        /// <summary>
        /// Mapping spécifique OU → Enseignant pour cet import
        /// </summary>
        [JsonPropertyName("ouTeacherMappings")]
        public Dictionary<string, string> OUTeacherMappings { get; set; } = new();
        */

/// <summary>
///     Configuration des dossiers Teams à créer
/// </summary>
[JsonPropertyName("folderMappings")]
    public List<TeamsFolderMapping> FolderMappings { get; set; } = new();
}

/// <summary>
///     Configuration des dossiers à créer dans Teams
/// </summary>
public class TeamsFolderMapping
{
    /// <summary>
    ///     Nom du dossier à créer
    /// </summary>
    [JsonPropertyName("folderName")]
    public string FolderName { get; set; } = string.Empty;

    /// <summary>
    ///     Description du dossier
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    ///     Dossier parent (optionnel)
    /// </summary>
    [JsonPropertyName("parentFolder")]
    public string? ParentFolder { get; set; }

    /// <summary>
    ///     Permissions par défaut pour ce dossier
    /// </summary>
    [JsonPropertyName("defaultPermissions")]
    public TeamsFolderPermissions? DefaultPermissions { get; set; }

    /// <summary>
    ///     Ordre d'affichage
    /// </summary>
    [JsonPropertyName("order")]
    public int Order { get; set; } = 0;

    /// <summary>
    ///     Active/désactive ce dossier
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>
///     Permissions pour les dossiers Teams
/// </summary>
public class TeamsFolderPermissions
{
    [JsonPropertyName("canRead")] public bool CanRead { get; set; } = true;

    [JsonPropertyName("canWrite")] public bool CanWrite { get; set; } = false;

    [JsonPropertyName("canDelete")] public bool CanDelete { get; set; } = false;

    [JsonPropertyName("canCreateSubfolders")]
    public bool CanCreateSubfolders { get; set; } = false;
}

/// <summary>
///     Configuration Azure AD globale
/// </summary>
public class AzureADConfig
{
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }

    [JsonPropertyName("tenantId")] public string? TenantId { get; set; }

    [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
}

/// <summary>
///     Configuration globale Graph API
/// </summary>
public class GraphApiConfig
{
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("maxRetryAttempts")] public int MaxRetryAttempts { get; set; } = 3;

    [JsonPropertyName("retryDelayMs")] public int RetryDelayMs { get; set; } = 5000;
}

/// <summary>
///     Configuration Teams globale (simplifiée)
/// </summary>
public class TeamsIntegrationConfig
{
    /// <summary>
    ///     Active l'intégration Teams globalement
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     OUs à exclure globalement de la création automatique Teams
    /// </summary>
    [JsonPropertyName("excludedOUs")]
    public List<string> ExcludedOUs { get; set; } = new();

    /// <summary>
    ///     Mappings OU vers Teams (pour le suivi des équipes créées)
    /// </summary>
    [JsonPropertyName("mappings")]
    public List<OUTeamsMapping> Mappings { get; set; } = new();
}

/// <summary>
///     Résultat de la création d'une équipe Teams
/// </summary>
public class TeamsCreationResult
{
    public bool Success { get; set; }
    public string? TeamId { get; set; }
    public string? GroupId { get; set; }
    public string? ClassName { get; set; }
    public string? OUPath { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int AttemptsCount { get; set; } = 1;
    public Dictionary<string, object> AdditionalInfo { get; set; } = new();
    public List<string> CreatedFolders { get; set; } = new(); // Nouveaux dossiers créés
}

/// <summary>
///     Mapping entre une OU Active Directory et une équipe Teams
/// </summary>
public class OUTeamsMapping
{
    [Required]
    [JsonPropertyName("ouDistinguishedName")]
    public string OUDistinguishedName { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = string.Empty;

    [JsonPropertyName("className")] public string ClassName { get; set; } = string.Empty;

    [JsonPropertyName("teacherUserId")] public string? TeacherUserId { get; set; }

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastSyncAt")] public DateTime LastSyncAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("isActive")] public bool IsActive { get; set; } = true;

    [JsonPropertyName("memberCount")] public int MemberCount { get; set; } = 0;

    /// <summary>
    ///     Métadonnées additionnelles pour le mapping
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    ///     Import ID qui a créé cette équipe
    /// </summary>
    [JsonPropertyName("importId")]
    public string? ImportId { get; set; }
}

/// <summary>
///     Demande de création d'équipe Teams
/// </summary>
public class TeamCreationRequest
{
    [Required] public string OUName { get; set; } = string.Empty;

    [Required] public string OUPath { get; set; } = string.Empty;

    public string? TeacherUserId { get; set; }
    public string? CustomTeamName { get; set; }
    public string? CustomDescription { get; set; }
    public List<string> InitialMemberIds { get; set; } = new();
    public bool CreateDefaultChannels { get; set; } = true;
    public bool SetupDefaultFolders { get; set; } = true;
    public Dictionary<string, string> AdditionalSettings { get; set; } = new();
}

/// <summary>
///     Statut de santé de l'intégration Teams
/// </summary>
public class TeamsIntegrationHealthStatus
{
    public bool IsHealthy { get; set; }
    public bool GraphApiConnected { get; set; }
    public bool LdapConnected { get; set; }
    public bool Enabled { get; set; } = false;
    public string Status { get; set; } = string.Empty;
    public List<string> Issues { get; set; } = new();
    public DateTime LastCheckAt { get; set; } = DateTime.UtcNow;
    public int ActiveMappingsCount { get; set; }
    public int PendingOperationsCount { get; set; }
    public Dictionary<string, object> Metrics { get; set; } = new();
}

/// <summary>
///     Opération en attente pour retry
/// </summary>
public class PendingTeamsOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public TeamsOperationType OperationType { get; set; }
    public string OUPath { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public int AttemptsCount { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextRetryAt { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
}

/// <summary>
///     Types d'opérations Teams
/// </summary>
public enum TeamsOperationType
{
    CreateTeam,
    AddUserToTeam,
    SyncUsers,
    UpdateTeamSettings,
    CreateChannel,
    SetPermissions
}

/// <summary>
///     Résultat d'une synchronisation d'utilisateurs
/// </summary>
public class UserSyncResult
{
    public string UserId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public UserSyncAction Action { get; set; }
}

/// <summary>
///     Actions de synchronisation utilisateur
/// </summary>
public enum UserSyncAction
{
    Added,
    Removed,
    Updated,
    Skipped,
    Failed
}

/// <summary>
///     Statistiques d'intégration Teams
/// </summary>
public class TeamsIntegrationStats
{
    public int TotalOUs { get; set; }
    public int OUsWithTeams { get; set; }
    public int TeamsCreated { get; set; }
    public int UsersAddedToTeams { get; set; }
    public int FailedOperations { get; set; }
    public DateTime LastSync { get; set; }
    public double SuccessRate { get; set; }
    public Dictionary<string, int> OperationCounts { get; set; } = new();
}

/// <summary>
///     Modèle de requête pour ajouter un utilisateur à une équipe
/// </summary>
public class AddUserToTeamRequest
{
    [Required] [StringLength(50)] public string SamAccountName { get; set; } = string.Empty;

    [Required] [StringLength(500)] public string OUDistinguishedName { get; set; } = string.Empty;
}