namespace ADManagerAPI.Models;

public class RemoteServerSettings
{
    public string? TargetServerName { get; set; }
    public bool UseExplicitCredentials { get; set; } = false;
    public string? RemoteUser { get; set; }
    public string? RemotePassword { get; set; }
}

public class FolderManagementSettings
{
    public string BaseStudentPath { get; set; } = "D:\\Students";
    public string BaseClassGroupPath { get; set; } = "D:\\Classes";
    public List<FolderTemplate> Templates { get; set; } = new();
    public RemoteServerSettings? RemoteServerSettings { get; set; }
    public object? AdminShareLetter { get; set; } = "C:";
}

public class FolderPermission
{
    public string IdentityReference { get; set; }
    public string FileSystemRights { get; set; }
    public string AccessControlType { get; set; }
    public string? InheritanceFlags { get; set; }
    public string? PropagationFlags { get; set; }
}

public class SubFolderDefinition
{
    public string Path { get; set; }
    public List<FolderPermission> Permissions { get; set; } = new();
}

public class FolderTemplate
{
    public string Name { get; set; }
    public TemplateType Type { get; set; }
    public List<SubFolderDefinition> SubFolders { get; set; } = new();
}

public enum TemplateType
{
    Student,
    ClassGroup
}

public enum UserRole
{
    Student,
    Professor,
    Administrative
}

public class StudentInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
}

public class ClassGroupInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
}