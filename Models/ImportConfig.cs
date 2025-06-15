namespace ADManagerAPI.Models
{
    public class FolderConfig
    {
        /// <summary>
        /// Template pour le chemin UNC du dossier personnel de l'utilisateur.
        /// Placeholders supportés: %username%, %givenName%, %sn%, %division%, %studentId%, %initials%.
        /// Exemple: "\\SERVER\Users\%division%\%username%"
        /// </summary>
        public string? HomeDirectoryTemplate { get; set; }

        /// <summary>
        /// Lettre de lecteur pour l'attribut homeDrive (ex: "H:").
        /// </summary>
        public string? HomeDriveLetter { get; set; }

        /// <summary>
        /// Valeur à utiliser pour le placeholder %division% si la 'division' de l'utilisateur est vide.
        /// </summary>
        public string? DefaultDivisionValue { get; set; }

        /// <summary>
        /// Nom du serveur cible pour exécuter les opérations de création de dossier.
        /// </summary>
        public string? TargetServerName { get; set; }

        /// <summary>
        /// Nom du partage principal sous lequel les dossiers utilisateurs seront créés.
        /// </summary>
        public string? ShareNameForUserFolders { get; set; }

        /// <summary>
        /// Chemin physique local sur le serveur cible.
        /// </summary>
        public string? LocalPathForUserShareOnServer { get; set; }

        /// <summary>
        /// Active ou désactive la fonctionnalité de provisionnement de partage utilisateur.
        /// </summary>
        public bool EnableShareProvisioning { get; set; } = true;

        /// <summary>
        /// Liste des sous-dossiers à créer par défaut dans chaque dossier utilisateur partagé.
        /// </summary>
        public List<string>? DefaultShareSubfolders { get; set; }
    }

    public class ClassGroupFolderCreationConfig
    {
        public string? CreateClassGroupFolderColumnName { get; set; } 
        public string? ClassGroupIdColumnName { get; set; }        
        public string? ClassGroupNameColumnName { get; set; }    
        public string? ClassGroupTemplateNameColumnName { get; set; }
    }

    public class TeamGroupCreationConfig
    {
        public string? CreateTeamGroupColumnName { get; set; }
        public string? TeamGroupNameColumnName { get; set; } 
    }

    public class GroupManagementConfig
    {
        public bool EnableGroupNesting { get; set; } = false;
        public string? GroupNestingTarget { get; set; }
        public string? GroupDescriptionTemplate { get; set; }
    }

    public partial class ImportConfig
    {
        public Dictionary<string, string> Mappings { get; set; } = new();
        public FolderConfig? Folders { get; set; }
        public ClassGroupFolderCreationConfig? ClassGroupFolderCreationConfig { get; set; }
        public TeamGroupCreationConfig? TeamGroupCreationConfig { get; set; }
        public string? NetBiosDomainName { get; set; }
        public string? GroupPrefix { get; set; }
        public GroupManagementConfig? GroupManagement { get; set; }
    }
} 