using System.Text.Json.Serialization;

namespace ADManagerAPI.Models
{
    
    public enum ActionType
    {
        CREATE_GROUP,
        CREATE_USER,
        UPDATE_USER,
        DELETE_USER,
        MOVE_USER,
        CREATE_OU,
        UPDATE_OU,
        DELETE_OU,
        CREATE_STUDENT_FOLDER,
        CREATE_TEAM,
        CREATE_CLASS_GROUP_FOLDER,
        ADD_USER_TO_GROUP,
        ERROR,
    }

    public partial class ImportConfig
    {
        [JsonPropertyName("createMissingOUs")]
        public bool CreateMissingOUs { get; set; } = true;
        
        [JsonPropertyName("defaultOU")]
        public string DefaultOU { get; set; } = "DC=domain,DC=local";
        
        [JsonPropertyName("overwriteExisting")]
        public bool OverwriteExisting { get; set; } = true;
        
        [JsonPropertyName("moveObjects")]
        public bool MoveObjects { get; set; } = false;
        
        [JsonPropertyName("deleteNotInImport")]
        public bool DeleteNotInImport { get; set; } = false;

        [JsonPropertyName("csvDelimiter")] public char CsvDelimiter { get; set; } = ';';
        
        [JsonPropertyName("headerMapping")]
        public Dictionary<string, string> HeaderMapping { get; set; } = new Dictionary<string, string>();
        
        [JsonPropertyName("skipErrors")]
        public bool SkipErrors { get; set; } = false;
        
        [JsonPropertyName("manualColumns")]
        public List<string> ManualColumns { get; set; } = new List<string>();
        
        [JsonPropertyName("ouColumn")]
        public string ouColumn { get; set; } = string.Empty;
        
        [JsonPropertyName("samAccountNameColumn")]
        public string SamAccountNameColumn { get; set; } = "sAMAccountName";
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
        [JsonPropertyName("creates")]
        public int CreateCount { get; set; } = 0;
        
        [JsonPropertyName("updates")]
        public int UpdateCount { get; set; } = 0;
        
        [JsonPropertyName("deletes")]
        public int DeleteCount { get; set; } = 0;
        
        [JsonPropertyName("skipped")]
        public int Skipped { get; set; } = 0;
        
        [JsonPropertyName("errors")]
        public int Errors { get; set; } = 0;
        
        [JsonPropertyName("success")]
        public int Success { get; set; } = 0;
        
        [JsonPropertyName("total")]
        public int Total => CreateCount + UpdateCount + DeleteCount + Skipped + Errors;
    }
    
    
    public class DetailedImportResult
    {
        public bool Success { get; set; } = true;
        public int CreatedCount { get; set; } = 0;
        public int UpdatedCount { get; set; } = 0;
        public int DeletedCount { get; set; } = 0;
        public int MovedCount { get; set; } = 0;
        public int ErrorCount { get; set; } = 0;
        public DetailedImportSummary Summary { get; set; } = new DetailedImportSummary();
        public List<ImportActionResult> Details { get; set; } = new List<ImportActionResult>();
    }

 

    
    public class ActionItem
    {
        [JsonPropertyName("action")]
        public string Action { get; set; }
        
        [JsonPropertyName("objectName")]
        public string ObjectName { get; set; }
        
        [JsonPropertyName("path")]
        public string Path { get; set; }
        
        [JsonPropertyName("message")]
        public string Message { get; set; }
        
        [JsonPropertyName("originalData")]
        public Dictionary<string, string> OriginalData { get; set; }
        
        [JsonPropertyName("targetData")]
        public Dictionary<string, string> TargetData { get; set; }
        
        [JsonPropertyName("status")]
        public string Status { get; set; }
        
        [JsonPropertyName("actionType")]
        public int? ActionType { get; set; }
    }
    
    public class ImportPreview
    {
        [JsonPropertyName("actions")]
        public List<ActionItem> Actions { get; set; } = new List<ActionItem>();
        
        [JsonPropertyName("summary")]
        public ActionSummary Summary { get; set; } = new ActionSummary();
    }
    
    public class OrganizationalUnit
    {
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }
        
        [JsonPropertyName("path")]
        public string Path { get; set; }
        public string Description { get; set; }
    }
    
    public class SavedImportConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
        
        [JsonPropertyName("name")]
        [System.ComponentModel.DataAnnotations.Required]
        public string Name { get; set; } = "";
        
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
        
        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; set; } = "";
        
        [JsonPropertyName("configData")]
        [System.ComponentModel.DataAnnotations.Required]
        public ImportConfig ConfigData { get; set; }
        
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class ImportConfigDto
    {
        [JsonPropertyName("objectType")]
        public string ObjectTypeStr { get; set; } = "User";
        
        [JsonPropertyName("csvDelimiter")]
        public char CsvDelimiter { get; set; } = ';';
        
        [JsonPropertyName("headerMapping")]
        public Dictionary<string, string> HeaderMapping { get; set; } = new Dictionary<string, string>();
        
        [JsonPropertyName("createMissingOUs")]
        public bool CreateMissingOUs { get; set; } = false;
        
        [JsonPropertyName("defaultOU")]
        public string DefaultOU { get; set; } = "";
        
        [JsonPropertyName("overwriteExisting")]
        public bool OverwriteExisting { get; set; } = true;
        
        [JsonPropertyName("moveObjects")]
        public bool MoveObjects { get; set; } = false;
        
        [JsonPropertyName("deleteNotInImport")]
        public bool DeleteNotInImport { get; set; } = false;
        
        [JsonPropertyName("dryRun")]
        public bool DryRun { get; set; } = true;
        
        [JsonPropertyName("manualColumns")]
        public List<string> ManualColumns { get; set; } = new List<string>();
        
        [JsonPropertyName("ouColumn")]
        public string ouColumn { get; set; } = "";
        
    
        public ClassGroupFolderCreationConfig? ClassGroupFolderCreationConfig { get; set; }
        public TeamGroupCreationConfig? TeamGroupCreationConfig { get; set; }
        public FolderConfig? Folders { get; set; }
        public string? NetBiosDomainName { get; set; }
   
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
                ClassGroupFolderCreationConfig = this.ClassGroupFolderCreationConfig,
                TeamGroupCreationConfig = this.TeamGroupCreationConfig,
                Folders = this.Folders ?? new FolderConfig(),
                NetBiosDomainName = this.NetBiosDomainName
            };
        }
    }

    public class SavedImportConfigDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
        
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
        
        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; set; } = "";
        
        [JsonPropertyName("configData")]
        public ImportConfigDto ConfigData { get; set; }
        
        [JsonPropertyName("createdAt")]
        public string CreatedAtStr { get; set; } = "";
        

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
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
        public bool Selected { get; set; } = true;
        
        [JsonIgnore]
        public string ActionTypeString => ActionType.ToString();
    }

    public class ImportSummary 
    {
        [JsonPropertyName("totalObjects")]
        public int TotalObjects { get; set; } = 0;
        
        [JsonPropertyName("createCount")]
        public int CreateCount { get; set; } = 0;
        
        [JsonPropertyName("updateCount")]
        public int UpdateCount { get; set; } = 0;
        
        [JsonPropertyName("deleteCount")]
        public int DeleteCount { get; set; } = 0;
        
        [JsonPropertyName("errorCount")]
        public int ErrorCount { get; set; } = 0;
        
        [JsonPropertyName("createOUCount")]
        public int CreateOUCount { get; set; } = 0;

        [JsonPropertyName("deleteOUCount")]
        public int DeleteOUCount { get; set; } = 0;

        [JsonPropertyName("moveCount")]
        public int MoveCount { get; set; } = 0;
        
        [JsonPropertyName("processedCount")]
        public int ProcessedCount { get; set; } = 0;

        [JsonPropertyName("createStudentFolderCount")]
        public int CreateStudentFolderCount { get; set; } = 0;

        [JsonPropertyName("createClassGroupFolderCount")]
        public int CreateClassGroupFolderCount { get; set; } = 0;

        [JsonPropertyName("createTeamGroupCount")]
        public int CreateTeamGroupCount { get; set; } = 0;

        [JsonPropertyName("provisionUserShareCount")]
        public int ProvisionUserShareCount { get; set; } = 0;
    }
} 