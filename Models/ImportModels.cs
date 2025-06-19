using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ADManagerAPI.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionType
{
    CREATE_GROUP,
    CREATE_USER,
    UPDATE_USER,
    DELETE_USER,
    DELETE_GROUP,
    MOVE_USER,
    CREATE_OU,
    UPDATE_OU,
    DELETE_OU,
    CREATE_STUDENT_FOLDER,
    CREATE_TEAM,
    CREATE_CLASS_GROUP_FOLDER,
    ADD_USER_TO_GROUP,
    ERROR
}

public partial class ImportConfig
{
    [JsonPropertyName("createMissingOUs")] public bool CreateMissingOUs { get; set; } = true;

    [JsonPropertyName("defaultOU")] public string DefaultOU { get; set; } = "DC=domain,DC=local";

    [JsonPropertyName("overwriteExisting")]
    public bool OverwriteExisting { get; set; } = true;

    [JsonPropertyName("moveObjects")] public bool MoveObjects { get; set; }

    [JsonPropertyName("deleteNotInImport")]
    public bool DeleteNotInImport { get; set; }

    [JsonPropertyName("csvDelimiter")] public char CsvDelimiter { get; set; } = ';';

    [JsonPropertyName("headerMapping")] public Dictionary<string, string> HeaderMapping { get; set; } = new();

    [JsonPropertyName("skipErrors")] public bool SkipErrors { get; set; } = false;

    [JsonPropertyName("manualColumns")] public List<string> ManualColumns { get; set; } = new();

    [JsonPropertyName("ouColumn")] public string ouColumn { get; set; } = string.Empty;

    [JsonPropertyName("samAccountNameColumn")]
    public string SamAccountNameColumn { get; set; } = "sAMAccountName";

    [JsonPropertyName("disabledActionTypes")]
    public List<ActionType> DisabledActionTypes { get; set; } = new();
    
    /// <summary>
    /// Mot de passe par défaut à utiliser pour tous les nouveaux comptes utilisateurs
    /// </summary>
    [JsonPropertyName("defaultPassword")]
    public string DefaultPassword { get; set; } = "TempPass123!";
}

public class FolderSettings
{
    public string HomeDirectoryTemplate { get; set; }

    public string HomeDriveLetter { get; set; }

    public string? DefaultDivisionValue { get; set; }

    public string? TargetServerName { get; set; }

    public string? ShareNameForUserFolders { get; set; }

    public string? LocalPathForUserShareOnServer { get; set; }

    public bool EnableShareProvisioning { get; set; } = true;

    public List<string>? DefaultShareSubfolders { get; set; }
}

public class ActionSummary
{
    [JsonPropertyName("creates")] public int CreateCount { get; set; } = 0;

    [JsonPropertyName("updates")] public int UpdateCount { get; set; } = 0;

    [JsonPropertyName("deletes")] public int DeleteCount { get; set; } = 0;

    [JsonPropertyName("skipped")] public int Skipped { get; set; } = 0;

    [JsonPropertyName("errors")] public int Errors { get; set; } = 0;

    [JsonPropertyName("success")] public int Success { get; set; } = 0;

    [JsonPropertyName("total")] public int Total => CreateCount + UpdateCount + DeleteCount + Skipped + Errors;
}

public class DetailedImportResult
{
    public bool Success { get; set; } = true;
    public int CreatedCount { get; set; } = 0;
    public int UpdatedCount { get; set; } = 0;
    public int DeletedCount { get; set; } = 0;
    public int MovedCount { get; set; } = 0;
    public int ErrorCount { get; set; } = 0;
    public DetailedImportSummary Summary { get; set; } = new();
    public List<ImportActionResult> Details { get; set; } = new();
}

public class ActionItem
{
    [JsonPropertyName("action")] public string Action { get; set; }

    [JsonPropertyName("objectName")] public string ObjectName { get; set; }

    [JsonPropertyName("path")] public string Path { get; set; }

    [JsonPropertyName("message")] public string Message { get; set; }

    [JsonPropertyName("originalData")] public Dictionary<string, string> OriginalData { get; set; }

    [JsonPropertyName("targetData")] public Dictionary<string, string> TargetData { get; set; }

    [JsonPropertyName("status")] public string Status { get; set; }

    [JsonPropertyName("actionType")] public int? ActionType { get; set; }
}

public class ImportPreview
{
    [JsonPropertyName("actions")] public List<ActionItem> Actions { get; set; } = new();

    [JsonPropertyName("summary")] public ActionSummary Summary { get; set; } = new();
}

public class OrganizationalUnit
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; }

    [JsonPropertyName("path")] public string Path { get; set; }

    public string Description { get; set; }
}

public class FolderMapping
{
    [JsonPropertyName("folderName")] public string FolderName { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("parentFolder")] public string? ParentFolder { get; set; }

    [JsonPropertyName("order")] public int Order { get; set; } = 1;

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("defaultPermissions")]
    public FolderPermissions DefaultPermissions { get; set; } = new();
}

public class FolderPermissions
{
    [JsonPropertyName("canRead")] public bool CanRead { get; set; } = true;

    [JsonPropertyName("canWrite")] public bool CanWrite { get; set; } = true;

    [JsonPropertyName("canDelete")] public bool CanDelete { get; set; } = false;

    [JsonPropertyName("canCreateSubfolders")]
    public bool CanCreateSubfolders { get; set; } = true;
}

/*
public class TeamsImportConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;



    [JsonPropertyName("autoAddUsersToTeams")]
    public bool AutoAddUsersToTeams { get; set; } = true;

    [JsonPropertyName("defaultTeacherUserId")]
    public string? DefaultTeacherUserId { get; set; }

    [JsonPropertyName("teamNamingTemplate")]
    public string TeamNamingTemplate { get; set; } = "Classe {OUName} - Année {Year}";

    [JsonPropertyName("teamDescriptionTemplate")]
    public string TeamDescriptionTemplate { get; set; } = "Équipe collaborative pour la classe {OUName}";

    [JsonPropertyName("ouTeacherMappings")]
    public Dictionary<string, string> OuTeacherMappings { get; set; } = new();

    [JsonPropertyName("folderMappings")]
    public List<FolderMapping> FolderMappings { get; set; } = new();
}
*/

public class SavedImportConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] [Required] public string Name { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("createdBy")] public string CreatedBy { get; set; } = "";

    [JsonPropertyName("configData")]
    [Required]
    public ImportConfig ConfigData { get; set; }

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("category")] public string Category { get; set; } = "Custom";
}

public class ImportConfigDto
{
    [JsonPropertyName("objectType")] public string ObjectTypeStr { get; set; } = "User";

    [JsonPropertyName("csvDelimiter")] public char CsvDelimiter { get; set; } = ';';

    [JsonPropertyName("headerMapping")] public Dictionary<string, string> HeaderMapping { get; set; } = new();

    [JsonPropertyName("createMissingOUs")] public bool CreateMissingOUs { get; set; } = false;

    [JsonPropertyName("defaultOU")] public string DefaultOU { get; set; } = "";

    [JsonPropertyName("overwriteExisting")]
    public bool OverwriteExisting { get; set; } = true;

    [JsonPropertyName("moveObjects")] public bool MoveObjects { get; set; } = false;

    [JsonPropertyName("deleteNotInImport")]
    public bool DeleteNotInImport { get; set; } = false;

    [JsonPropertyName("dryRun")] public bool DryRun { get; set; } = true;

    [JsonPropertyName("manualColumns")] public List<string> ManualColumns { get; set; } = new();

    [JsonPropertyName("ouColumn")] public string ouColumn { get; set; } = "";


    [JsonPropertyName("classGroupFolderCreationConfig")]
    public ClassGroupFolderCreationConfig? ClassGroupFolderCreationConfig { get; set; }

    // ✅ SUPPRIMÉ : teamGroupCreationConfig remplacé par teamsIntegration

    [JsonPropertyName("folders")] public FolderConfig? Folders { get; set; }

    [JsonPropertyName("netBiosDomainName")]
    public string? NetBiosDomainName { get; set; }

    [JsonPropertyName("teamsIntegration")] public TeamsImportConfig? TeamsIntegration { get; set; }

    [JsonPropertyName("disabledActionTypes")]
    public List<ActionType> DisabledActionTypes { get; set; } = new();
    
    /// <summary>
    /// Mot de passe par défaut à utiliser pour tous les nouveaux comptes utilisateurs
    /// </summary>
    [JsonPropertyName("defaultPassword")]
    public string DefaultPassword { get; set; } = "TempPass123!";

    public ImportConfig ToImportConfig()
    {
        return new ImportConfig
        {
            CsvDelimiter = CsvDelimiter,
            HeaderMapping = HeaderMapping,
            CreateMissingOUs = CreateMissingOUs,
            DefaultOU = DefaultOU,
            OverwriteExisting = OverwriteExisting,
            MoveObjects = MoveObjects,
            DeleteNotInImport = DeleteNotInImport,
            ManualColumns = ManualColumns,
            ouColumn = ouColumn,
            ClassGroupFolderCreationConfig = ClassGroupFolderCreationConfig,
            // ✅ SUPPRIMÉ : TeamGroupCreationConfig remplacé par TeamsIntegration
            Folders = Folders ?? new FolderConfig(),
            NetBiosDomainName = NetBiosDomainName,
            TeamsIntegration = TeamsIntegration,
            DisabledActionTypes = DisabledActionTypes,
            DefaultPassword = DefaultPassword
        };
    }
}

public class SavedImportConfigDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("createdBy")] public string CreatedBy { get; set; } = "";

    [JsonPropertyName("configData")] public ImportConfigDto ConfigData { get; set; }

    [JsonPropertyName("createdAt")] public string CreatedAtStr { get; set; } = "";


    public SavedImportConfig ToSavedImportConfig()
    {
        DateTime.TryParse(CreatedAtStr, out var createdAt);

        return new SavedImportConfig
        {
            Id = Id,
            Name = Name,
            Description = Description,
            CreatedBy = CreatedBy,
            ConfigData = ConfigData?.ToImportConfig(),
            CreatedAt = createdAt != default ? createdAt : DateTime.Now
        };
    }
}

public class ImportProgress
{
    public int Progress { get; set; }

    public string Status { get; set; } = "processing";

    public string Message { get; set; } = "";

    public object? Analysis { get; set; }

    public object? Result { get; set; }

    public int? TotalActions { get; set; }

    public int? CurrentAction { get; set; }
}

public class ImportActionItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public ActionType ActionType { get; set; }
    public string ObjectName { get; set; }
    public string Path { get; set; }
    public string Message { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new();
    public bool Selected { get; set; } = true;

    [JsonIgnore] public string ActionTypeString => ActionType.ToString();
}

public class ImportSummary
{
    [JsonPropertyName("totalObjects")] public int TotalObjects { get; set; } = 0;

    [JsonPropertyName("createCount")] public int CreateCount { get; set; } = 0;

    [JsonPropertyName("updateCount")] public int UpdateCount { get; set; } = 0;

    [JsonPropertyName("deleteCount")] public int DeleteCount { get; set; } = 0;

    [JsonPropertyName("errorCount")] public int ErrorCount { get; set; } = 0;

    [JsonPropertyName("createOUCount")] public int CreateOUCount { get; set; } = 0;

    [JsonPropertyName("deleteOUCount")] public int DeleteOUCount { get; set; } = 0;

    [JsonPropertyName("deleteGroupCount")] public int DeleteGroupCount { get; set; } = 0;

    [JsonPropertyName("moveCount")] public int MoveCount { get; set; } = 0;

    [JsonPropertyName("processedCount")] public int ProcessedCount { get; set; } = 0;

    [JsonPropertyName("createStudentFolderCount")]
    public int CreateStudentFolderCount { get; set; } = 0;

    [JsonPropertyName("createClassGroupFolderCount")]
    public int CreateClassGroupFolderCount { get; set; } = 0;

    [JsonPropertyName("createTeamGroupCount")]
    public int CreateTeamGroupCount { get; set; } = 0;

    [JsonPropertyName("provisionUserShareCount")]
    public int ProvisionUserShareCount { get; set; } = 0;
}

public class ImportConfigTemplate
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("category")] public string Category { get; set; } = "Custom";

    [JsonPropertyName("isSystemTemplate")] public bool IsSystemTemplate { get; set; } = false;

    [JsonPropertyName("configData")] public ImportConfig ConfigData { get; set; } = new();
}

public class ConfigValidationResult
{
    [JsonPropertyName("isValid")] public bool IsValid { get; set; } = true;

    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();

    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}

public class DuplicateConfigRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class CreateFromTemplateRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("description")] public string? Description { get; set; }
}