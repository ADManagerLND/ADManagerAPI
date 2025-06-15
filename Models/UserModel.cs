using System.Collections.Generic;

namespace ADManagerAPI.Models
{
    public class UserModel
    {
        public string SamAccountName { get; set; }
        public string DisplayName { get; set; }
        public string GivenName { get; set; }
        public string Surname { get; set; }
        public string UserPrincipalName { get; set; }
        public string Email { get; set; }
        public string Description { get; set; }
        public string OrganizationalUnit { get; set; }
        public bool Enabled { get; set; }
        public Dictionary<string, string> AdditionalAttributes { get; set; } = new();
        public string DistinguishedName { get; set; } = string.Empty;
    }
}