using System.Collections.Generic;

namespace ADManagerAPI.Models
{
    public class RemoteServerSettings
    {
        public string? TargetServerName { get; set; } // Null or empty means local execution
        public bool UseExplicitCredentials { get; set; } = false;
        public string? RemoteUser { get; set; } // Required if UseExplicitCredentials is true
        public string? RemotePassword { get; set; } // Required if UseExplicitCredentials is true. STORE SECURELY!
    }

    public class FolderManagementSettings
    {
        public string BaseStudentPath { get; set; } = "D:\\Students"; // Example default
        public string BaseClassGroupPath { get; set; } = "D:\\Classes"; // Example default
        public List<FolderTemplate> Templates { get; set; } = new List<FolderTemplate>();
        public RemoteServerSettings? RemoteServerSettings { get; set; }
    }

    public class FolderPermission
    {
        public string IdentityReference { get; set; } // e.g., "DOMAIN\\UserOrGroup", "BUILTIN\\Administrators", "NT AUTHORITY\\SYSTEM"
        public string FileSystemRights { get; set; } // e.g., "FullControl", "Modify", "ReadAndExecute", "Write", "Read"
        public string AccessControlType { get; set; } // "Allow" or "Deny"
        public string? InheritanceFlags { get; set; } // e.g., "None", "ContainerInherit", "ObjectInherit"
        public string? PropagationFlags { get; set; } // e.g., "None", "NoPropagateInherit", "InheritOnly"
    }

    public class SubFolderDefinition
    {
        public string Path { get; set; }
        public List<FolderPermission> Permissions { get; set; } = new List<FolderPermission>();
    }

    public class FolderTemplate
    {
        public string Name { get; set; }
        public TemplateType Type { get; set; } // Student or ClassGroup
        public List<SubFolderDefinition> SubFolders { get; set; } = new List<SubFolderDefinition>(); // Changed from List<string> SubPaths
    }

    public enum TemplateType
    {
        Student,
        ClassGroup
    }

    public enum UserRole // This seems more related to FSRM or general user classification than just folder templates
    {
        Student,
        Professor,
        Administrative
    }

    public class StudentInfo
    {
        public string Id { get; set; } // e.g., student number or unique identifier
        public string Name { get; set; }
        // Add other relevant properties that might be used in folder names or paths
        // For example: public string Email { get; set; }
    }

    public class ClassGroupInfo
    {
        public string Id { get; set; } // e.g., class code or unique identifier
        public string Name { get; set; }
        // Add other relevant properties
        // For example: public string AcademicYear { get; set; }
    }
} 