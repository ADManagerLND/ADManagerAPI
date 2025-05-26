using System.Text.Json.Serialization;

namespace ADManagerAPI.Models
{
    public class ApplicationSettings
    {
        [JsonPropertyName("api")]
        public ApiSettings Api { get; set; } = new();

        [JsonPropertyName("ldap")]
        public LdapSettings Ldap { get; set; } = new();

        [JsonPropertyName("userAttributes")]
        public UserAttributes UserAttributes { get; set; } = new();

        [JsonPropertyName("imports")]
        public List<SavedImportConfig> Imports { get; set; } = [];

        [JsonPropertyName("folderManagement")]
        public FolderManagementSettings FolderManagement { get; set; } = new();

        [JsonPropertyName("fsrmSettings")]
        public FsrmSettings Fsrm { get; set; } = new();
    }

    public class ApiSettings
    {
        [JsonPropertyName("apiUrl")]
        public string ApiUrl { get; set; } = "https://api.admanager.local";

        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = "v1";

        [JsonPropertyName("apiTimeout")]
        public int ApiTimeout { get; set; } = 30;

        [JsonPropertyName("apiRateLimit")]
        public int ApiRateLimit { get; set; } = 100;

        [JsonPropertyName("enableLogging")]
        public bool EnableLogging { get; set; } = true;

        [JsonPropertyName("language")]
        public string Language { get; set; } = "fr";
        
        [JsonPropertyName("theme")]
        public string Theme { get; set; } = "light";
 
        [JsonPropertyName("itemsPerPage")]
        public int ItemsPerPage { get; set; } = 10;
        
        [JsonPropertyName("sessionTimeout")]
        public int SessionTimeout { get; set; } = 30;
        
        [JsonPropertyName("enableNotifications")]
        public bool EnableNotifications { get; set; } = true;
    }

    public class LdapSettings
    {
        [JsonPropertyName("ldapServer")]
        public string LdapServer { get; set; } = "ldap://admanager.local";
        
        [JsonPropertyName("ldapDomain")]
        public string LdapDomain { get; set; } = "ldap://admanager.local";
        
        [JsonPropertyName("ldapPort")]
        public int LdapPort { get; set; } = 389;
        
        [JsonPropertyName("ldapBaseDn")]
        public string LdapBaseDn { get; set; } = "dc=admanager,dc=local";
        
        [JsonPropertyName("ldapUsername")]
        public string LdapUsername { get; set; } = "cn=admin,dc=admanager,dc=local";
        
        [JsonPropertyName("ldapPassword")]
        public string LdapPassword { get; set; } = "";
        
        [JsonPropertyName("ldapSsl")]
        public bool LdapSsl { get; set; } = false;
        
        [JsonPropertyName("ldapPageSize")]
        public int LdapPageSize { get; set; } = 1000;
    }

    public class UserAttributes
    {
        [JsonPropertyName("attributes")]
        public List<AdAttributeDefinition> Attributes { get; set; } = [];
    }

    public class AdAttributeDefinition
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("description")]
        public required string Description { get; set; }

        [JsonPropertyName("syntax")]
        public required string Syntax { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }
    }

    public class AttributeDescription
    {
        /// <summary>
        /// Nom de l'attribut
        /// </summary>
        public required string Name { get; set; }
        
        /// <summary>
        /// Description de l'attribut
        /// </summary>
        public required string Description { get; set; }
        
        /// <summary>
        /// Syntaxe ou format attendu de l'attribut
        /// </summary>
        public required string Syntax { get; set; }
        
        /// <summary>
        /// Indique si l'attribut est obligatoire
        /// </summary>
        public bool IsRequired { get; set; }
        
        /// <summary>
        /// Type de données de l'attribut
        /// </summary>
        public string? DataType { get; set; }
        
        /// <summary>
        /// Valeur par défaut de l'attribut
        /// </summary>
        public string? DefaultValue { get; set; }
    }
} 