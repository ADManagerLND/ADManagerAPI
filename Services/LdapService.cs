using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;

namespace ADManagerAPI.Services
{
    public class LdapService : ILdapService, IDisposable
    {
        private readonly LdapConnection _connection;
        private readonly string _baseDn;
        private readonly ILogger<LdapService> _logger;

        public LdapService(IConfiguration config, ILogger<LdapService> logger)
        {
            _logger = logger;
            var server = config.GetValue<string>("LdapSettings:Server");
            var port   = config.GetValue<int>("LdapSettings:Port", 389);
            var user   = config.GetValue<string>("LdapSettings:Username");
            var pass   = config.GetValue<string>("LdapSettings:Password");
            _baseDn    = config.GetValue<string>("LdapSettings:BaseDn");

            var identifier = new LdapDirectoryIdentifier(server, port);
            _connection = new LdapConnection(identifier)
            {
                AuthType = AuthType.Negotiate,
                Credential = new NetworkCredential(user, pass)
            };

            if (config.GetValue<bool>("LdapSettings:UseSsl"))
            {
                _connection.SessionOptions.SecureSocketLayer = true;
                _connection.SessionOptions.VerifyServerCertificate += (_, __) => true;
            }

            try
            {
                _connection.Bind();
                _logger.LogInformation("LDAP bind successful to {Server}:{Port}, BaseDn={BaseDn}", server, port, _baseDn);
            }
            catch (LdapException ex)
            {
                _logger.LogError(ex, "LDAP bind failed: invalid credentials or connection issue");
                throw;
            }
        }

        public bool UserExists(string userDn)
        {
            try
            {
                var req = new SearchRequest(userDn, "(objectClass=user)", SearchScope.Base);
                var res = (SearchResponse)_connection.SendRequest(req);
                return res.Entries.Count > 0;
            }
            catch (DirectoryOperationException ex)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking user {Dn}", userDn);
                throw;
            }
        }

        public void CreateUser(string firstName, string lastName, string password, string ouDn)
        {
            var cn = Escape(firstName + " " + lastName);
            var dn = $"CN={cn},{ouDn}";
            var sam = GenerateSam(firstName, lastName);
            
            // Vérifier si _baseDn est null et utiliser un domaine par défaut si nécessaire
            string domainPart = "local";
            if (!string.IsNullOrEmpty(_baseDn))
            {
                domainPart = _baseDn.Replace(",DC=", ".").Replace("DC=", "");
            }
            else
            {
                _logger.LogWarning("BaseDn est null. Utilisation d'un domaine par défaut pour UPN: 'local'");
            }
            
            var upn = $"{firstName.ToLower()}.{lastName.ToLower()}@{domainPart}".ToLower();

            var attrs = new[]
            {
                new DirectoryAttribute("objectClass", new[] { "top", "person", "organizationalPerson", "user" }),
                new DirectoryAttribute("cn", cn),
                new DirectoryAttribute("sAMAccountName", sam),
                new DirectoryAttribute("userPrincipalName", upn),
                new DirectoryAttribute("givenName", firstName),
                new DirectoryAttribute("sn", lastName)
            };

            _connection.SendRequest(new AddRequest(dn, attrs));
            SetPassword(dn, password);
            EnableAccount(dn);
            _logger.LogInformation("User created {Dn}", dn);
        }

        public void CreateOrganizationalUnit(string ouPath)
        {
            var req = new AddRequest(ouPath, new DirectoryAttribute("objectClass", "organizationalUnit"));
            _connection.SendRequest(req);
            _logger.LogInformation("OU created {Dn}", ouPath);
        }

        public bool OrganizationalUnitExists(string ouDn)
        {
            try
            {
                var req = new SearchRequest(ouDn, "(objectClass=organizationalUnit)", SearchScope.Base);
                var res = (SearchResponse)_connection.SendRequest(req);
                return res.Entries.Count > 0;
            }
            catch (DirectoryOperationException ex)
            {
                return false;
            }
        }

        public LdapUser SearchUser(string firstName, string lastName)
        {
            var filter = $"(&(objectClass=user)(givenName={Escape(firstName)})(sn={Escape(lastName)}))";
            var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, new[] {"cn","distinguishedName"});
            var res = (SearchResponse)_connection.SendRequest(req);
            if (res.Entries.Count == 0) return null;
            var e = res.Entries[0];
            return new LdapUser
            {
                FullName = e.Attributes["cn"][0].ToString(),
                DistinguishedName = e.DistinguishedName
            };
        }

        public Task<bool> OrganizationalUnitExistsAsync(string ouPath)
            => Task.FromResult(OrganizationalUnitExists(ouPath));

        public Task<bool> UserExistsAsync(string samAccountName)
        {
            try
            {
                // S'assurer que _baseDn n'est pas null
                if (string.IsNullOrEmpty(_baseDn))
                {
                    _logger.LogWarning("Vérification d'existence d'utilisateur impossible: _baseDn est null");
                    return Task.FromResult(false);
                }
                
                var filter = $"(&(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
                var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, null);
                
                var res = (SearchResponse)_connection.SendRequest(req);
                return Task.FromResult(res.Entries.Count > 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking user existence {Sam}", samAccountName);
                return Task.FromResult(false);
            }
        }

     
        public UserModel CreateUser(string samAccountName, Dictionary<string, string> attributes, string ouDn)
        {
            // Récupération des informations essentielles
            attributes.TryGetValue("givenName", out var firstName);
            attributes.TryGetValue("sn", out var lastName);
            var password = attributes.ContainsKey("password")
                ? attributes["password"]
                : attributes.ContainsKey("userPassword")
                    ? attributes["userPassword"]
                    : null;

            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Les attributs doivent inclure 'givenName', 'sn' et 'password' (ou 'userPassword').");
            }

            // Création effective
            CreateUser(firstName, lastName, password, ouDn);

            // Lecture du DN créé
            try
            {
                // Vérifier si _baseDn est null - si c'est le cas, on utilise directement le DN de l'OU
                string searchBase;
                if (string.IsNullOrEmpty(_baseDn))
                {
                    _logger.LogWarning("BaseDn est null pour la recherche de l'utilisateur créé. Utilisation directe du DN de l'utilisateur.");
                    // On peut simplement retourner le modèle utilisateur sans rechercher le DN exact
                    return new UserModel
                    {
                        SamAccountName = samAccountName,
                        GivenName = firstName,
                        Surname = lastName,
                        DisplayName = firstName + " " + lastName,
                        UserPrincipalName = samAccountName + "@local",
                        OrganizationalUnit = ouDn,
                        AdditionalAttributes = new Dictionary<string, string>
                        {
                            ["distinguishedName"] = $"CN={firstName} {lastName},{ouDn}"
                        }
                    };
                }
                
                var filter = $"(&(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
                var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
                var res = (SearchResponse)_connection.SendRequest(req);
                var dn = res.Entries.Count > 0
                    ? res.Entries[0].DistinguishedName
                    : throw new InvalidOperationException($"Impossible de retrouver le DN pour {samAccountName} après création.");

                // Gestion du domaine pour UserPrincipalName
                string domainPart = "local";
                if (!string.IsNullOrEmpty(_baseDn))
                {
                    domainPart = _baseDn.Replace("DC=", "").Replace(",", ".");
                }
                else
                {
                    _logger.LogWarning("BaseDn est null. Utilisation d'un domaine par défaut pour UPN dans UserModel: 'local'");
                }

                // Construction du UserModel
                return new UserModel
                {
                    SamAccountName       = samAccountName,
                    GivenName            = firstName,
                    Surname              = lastName,
                    DisplayName          = firstName + " " + lastName,
                    UserPrincipalName    = samAccountName + "@" + domainPart,
                    OrganizationalUnit   = ouDn,
                    AdditionalAttributes = new Dictionary<string, string>
                    {
                        ["distinguishedName"] = dn
                    }
                };
            }
            catch (Exception ex) 
            {
                _logger.LogError(ex, "Erreur lors de la recherche de l'utilisateur après création: {Sam}", samAccountName);
                // Retourner un modèle minimal même en cas d'erreur
                return new UserModel
                {
                    SamAccountName = samAccountName,
                    GivenName = firstName,
                    Surname = lastName,
                    DisplayName = firstName + " " + lastName,
                    UserPrincipalName = samAccountName + "@local",
                    OrganizationalUnit = ouDn
                };
            }
        }


        public void UpdateUser(string samAccountName, Dictionary<string, string> attributes)
        {
            var dn = GetUserDn(samAccountName);
            foreach (var kv in attributes)
            {
                var mod = new ModifyRequest(dn, DirectoryAttributeOperation.Replace, kv.Key, kv.Value);
                _connection.SendRequest(mod);
                _logger.LogInformation("Updated {Attr} for {Dn}", kv.Key, dn);
            }
        }

        public async Task<List<OrganizationalUnitModel>> GetAllOrganizationalUnitsAsync()
        {
            var list = new List<OrganizationalUnitModel>();
            var req = new SearchRequest(_baseDn, "(objectClass=organizationalUnit)", SearchScope.Subtree, new[] {"ou"});
            var res = (SearchResponse)_connection.SendRequest(req);
            foreach (SearchResultEntry e in res.Entries)
            {
                list.Add(new OrganizationalUnitModel
                {
                    DistinguishedName = e.DistinguishedName,
                    Name = e.Attributes.Contains("ou") ? e.Attributes["ou"][0].ToString() : null
                });
            }
            return list;
        }

        public Task<bool> TestConnection()
        {
            try
            {
                var req = new SearchRequest(_baseDn, "(objectClass=*)", SearchScope.Base);
                _connection.SendRequest(req);
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public async Task<List<string>> GetUsersInOUAsync(string ouDn)
        {
            var list = new List<string>();
            var req = new SearchRequest(ouDn, "(objectClass=user)", SearchScope.OneLevel, new[] {"sAMAccountName"});
            var res = (SearchResponse)_connection.SendRequest(req);
            foreach (SearchResultEntry e in res.Entries)
                list.Add(e.Attributes["sAMAccountName"][0].ToString());
            return list;
        }

        public void DeleteUser(string samAccountName, string currentOuDn)
        {
            try 
            {
                // Vérifier si _baseDn est null
                if (string.IsNullOrEmpty(_baseDn))
                {
                    // Si nous avons l'OU courante, nous pouvons construire le DN de l'utilisateur
                    if (!string.IsNullOrEmpty(currentOuDn))
                    {
                        string firstName = string.Empty;
                        string lastName = string.Empty;
                        
                        // Tenter d'extraire le prénom et le nom du samAccountName (format typique: prenom.nom)
                        string[] parts = samAccountName.Split('.');
                        if (parts.Length >= 2)
                        {
                            firstName = parts[0];
                            firstName = char.ToUpper(firstName[0]) + firstName.Substring(1);
                            
                            lastName = parts[1];
                            lastName = char.ToUpper(lastName[0]) + lastName.Substring(1);
                            
                            // Si le nom contient des parties supplémentaires (ex: prenom.nom1-nom2)
                            if (parts.Length > 2)
                            {
                                for (int i = 2; i < parts.Length; i++)
                                {
                                    lastName += " " + parts[i];
                                }
                            }
                            
                            // Construire un DN basé sur le format courant
                            string cn = $"{firstName} {lastName}";
                            string dn = $"CN={cn},{currentOuDn}";
                            
                            try
                            {
                                var del = new DeleteRequest(dn);
                                _connection.SendRequest(del);
                                _logger.LogInformation("User deleted using constructed DN {Dn}", dn);
                                return;
                            }
                            catch (DirectoryOperationException ex)
                            {
                                _logger.LogWarning(ex, "Échec de la suppression avec le DN construit {Dn}. L'utilisateur n'existe probablement pas.", dn);
                            }
                        }
                    }
                    
                    _logger.LogWarning("BaseDn non configuré et impossible de construire un DN valide pour {Sam}", samAccountName);
                    return;
                }
                
                // Vérifier si l'utilisateur existe avant de tenter de le supprimer
                if (UserExistsAsync(samAccountName).GetAwaiter().GetResult())
                {
                    var dn = GetUserDn(samAccountName);
                    var del = new DeleteRequest(dn);
                    _connection.SendRequest(del);
                    _logger.LogInformation("User deleted {Dn}", dn);
                }
                else
                {
                    _logger.LogWarning("Tentative de suppression d'un utilisateur inexistant: {Sam}", samAccountName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la suppression de l'utilisateur {Sam}", samAccountName);
                throw;
            }
        }

        public Task DeleteUserAsync(string samAccountName, string currentOuDn)
        {
            DeleteUser(samAccountName, currentOuDn);
            return Task.CompletedTask;
        }

        public Task DeleteOrganizationalUnitAsync(string ouDn)
        {
            var del = new DeleteRequest(ouDn);
            _connection.SendRequest(del);
            _logger.LogInformation("OU deleted {Dn}", ouDn);
            return Task.CompletedTask;
        }

        public async Task<List<string>> GetOrganizationalUnitPathsRecursiveAsync(string baseOuDn)
        {
            var list = new List<string>();
            var req = new SearchRequest(baseOuDn, "(objectClass=organizationalUnit)", SearchScope.Subtree);
            var res = (SearchResponse)_connection.SendRequest(req);
            foreach (SearchResultEntry e in res.Entries)
                list.Add(e.DistinguishedName);
            return list;
        }

        public Task<bool> IsOrganizationalUnitEmptyAsync(string ouDn)
        {
            var req = new SearchRequest(ouDn, "(objectClass=*)", SearchScope.OneLevel);
            var res = (SearchResponse)_connection.SendRequest(req);
            return Task.FromResult(res.Entries.Count == 0);
        }

        public void DeleteOrganizationalUnit(string ouDn)
        {
            var del = new DeleteRequest(ouDn);
            _connection.SendRequest(del);
            _logger.LogInformation("OU deleted {Dn}", ouDn);
        }

        private string GetUserDn(string sam)
        {
            if (string.IsNullOrEmpty(_baseDn))
            {
                _logger.LogWarning("BaseDn est null lors de la recherche du DN de l'utilisateur {Sam}. Impossible de retrouver le DN exact.", sam);
                throw new InvalidOperationException($"BaseDn non configuré. Impossible de retrouver le DN pour {sam}");
            }
            
            var filter = $"(sAMAccountName={Escape(sam)})";
            var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, new[] {"distinguishedName"});
            var res = (SearchResponse)_connection.SendRequest(req);
            if (res.Entries.Count == 0) throw new InvalidOperationException($"User {sam} not found");
            return res.Entries[0].DistinguishedName;
        }

        private void SetPassword(string userDn, string password)
        {
            var pwd = Encoding.Unicode.GetBytes("\"" + password + "\"");
            _connection.SendRequest(new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, "unicodePwd", pwd));
            _logger.LogInformation("Password set for {Dn}", userDn);
        }

        private void EnableAccount(string userDn)
        {
            const int UF_ACCOUNTDISABLE = 0x0002;
            const int UF_NORMAL_ACCOUNT = 0x0200;
            var val = (UF_NORMAL_ACCOUNT & ~UF_ACCOUNTDISABLE).ToString();
            _connection.SendRequest(new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, "userAccountControl", val));
            _logger.LogInformation("Account enabled {Dn}", userDn);
        }

        private string Escape(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var sb = new StringBuilder();
            foreach (var c in input)
            {
                if (",+\"\\<>;=#*()/".Contains(c)) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        private string GenerateSam(string fn, string ln)
        {
            fn = fn.ToLower().Replace(" ", "").Replace("-", "");
            ln = ln.ToLower().Replace(" ", "").Replace("-", "");
            var s = fn + "." + ln;
            return s.Length <= 20 ? s : s.Substring(0, 20);
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _logger.LogDebug("LDAP connection disposed");
        }
    }

    public class LdapUser
    {
        public string FullName { get; set; }
        public string DistinguishedName { get; set; }
    }
}