using System.Collections.Generic;

namespace ADManagerAPI.Models
{
    public class FolderManagementSettings
    {
        public string BaseStudentPath { get; set; }
        public string BaseClassGroupPath { get; set; }
        public List<FolderTemplate> Templates { get; set; }
    }

    public class FolderTemplate
    {
        public string Name { get; set; }
        public TemplateType Type { get; set; } // Student or ClassGroup
        public List<string> SubPaths { get; set; } // Relative paths, can include placeholders like {CourseName}
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