using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ADManagerAPI.Models
{
    public class ApplicationSettings
    {
        [JsonPropertyName("api")]
        public ApiSettings Api { get; set; }

        [JsonPropertyName("ldap")]
        public LdapSettings Ldap { get; set; }

        [JsonPropertyName("userAttributes")]
        public UserAttributesConfig UserAttributes { get; set; }

        [JsonPropertyName("imports")]
        public List<SavedImportConfig> Imports { get; set; }

        [JsonPropertyName("folderManagement")]
        public FolderManagementSettings FolderManagementSettings { get; set; }

        [JsonPropertyName("fsrmSettings")]
        public FsrmSettings FsrmSettings { get; set; }

        [JsonPropertyName("netBiosDomainName")]
        public string NetBiosDomainName { get; set; }

        public ApplicationSettings()
        {
            Api = new ApiSettings();
            Ldap = new LdapSettings();
            UserAttributes = new UserAttributesConfig();
            Imports = new List<SavedImportConfig>();
            FolderManagementSettings = new FolderManagementSettings();
            FsrmSettings = new FsrmSettings();
            NetBiosDomainName = string.Empty;
        }
    }

    public class ApiSettings
    {
        [JsonPropertyName("apiUrl")]
        public string ApiUrl { get; set; } = "https://api.admanager.local";
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

    public class UserAttributesConfig
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