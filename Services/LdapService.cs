using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using System.DirectoryServices.AccountManagement;
using ADManagerAPI.Config;
using SearchScope = System.DirectoryServices.Protocols.SearchScope;
using System.Linq;

namespace ADManagerAPI.Services
{
    public partial class LdapService : ILdapService, IDisposable
    {
        public required LdapConnection _connection;
        public required string _baseDn;
        private readonly ILogger<LdapService> _logger;
        private readonly LdapSettingsProvider _ldapSettingsProvider;
        private bool _connectionInitialized = false;
        private readonly object _connectionLock = new object();
        private DateTime _lastConnectionAttempt = DateTime.MinValue;
        private readonly TimeSpan _retryInterval = TimeSpan.FromMinutes(5); // Retry chaque 5 minutes
        private bool _ldapAvailable = true;

        public LdapService(ILogger<LdapService> logger, LdapSettingsProvider ldapSettingsProvider) // 👈 NOUVEAU
        {
            _logger = logger;
            _ldapSettingsProvider = ldapSettingsProvider;
            _logger.LogInformation("LdapService initialisé avec lazy loading. Connexion établie au premier appel.");
        }

        /// <summary>
        /// Initialise la connexion LDAP de manière résiliente
        /// </summary>
        private async Task<bool> InitializeLdapConnection()
        {
            try
            {
                // Récupérer les paramètres depuis LdapSettingsProvider
                var server = await _ldapSettingsProvider.GetServerAsync();
                var port = await _ldapSettingsProvider.GetPortAsync();
                var user = await _ldapSettingsProvider.GetUsernameAsync();
                var pass = await _ldapSettingsProvider.GetPasswordAsync();
                _baseDn = await _ldapSettingsProvider.GetBaseDnAsync();
                var useSsl = await _ldapSettingsProvider.GetSslAsync();

                var identifier = new LdapDirectoryIdentifier(server, port);
                _connection = new LdapConnection(identifier)
                {
                    AuthType = AuthType.Negotiate,
                    Credential = new NetworkCredential(user, pass)
                };

                if (useSsl)
                {
                    _connection.SessionOptions.SecureSocketLayer = true;
                    _connection.SessionOptions.VerifyServerCertificate += (_, __) => true;
                }

                _connection.Bind();
                _logger.LogInformation("✅ LDAP bind successful to {Server}:{Port}, BaseDn={BaseDn}", server, port, _baseDn);
                _connectionInitialized = true;
                _ldapAvailable = true;
                return true;
            }
            catch (LdapException ex)
            {
                _logger.LogWarning(ex, "⚠️ LDAP bind failed: {Message}. Service continuera en mode dégradé.", ex.Message);
                _ldapAvailable = false;
                _connectionInitialized = false;
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Error initializing LDAP connection: {Message}. Service continuera en mode dégradé.", ex.Message);
                _ldapAvailable = false;
                _connectionInitialized = false;
                return false;
            }
        }

        /// <summary>
        /// Assure que la connexion LDAP est établie (lazy loading + retry logic)
        /// </summary>
        private async Task<bool> EnsureConnectionAsync()
        {
            // Si connexion déjà établie et disponible
            if (_connectionInitialized && _ldapAvailable && _connection != null)
            {
                return true;
            }

            // Vérifier si on doit retry (pas plus souvent que l'intervalle défini)
            if (!_ldapAvailable && DateTime.Now - _lastConnectionAttempt < _retryInterval)
            {
                _logger.LogDebug("LDAP indisponible, retry dans {Remaining}s", 
                    (_retryInterval - (DateTime.Now - _lastConnectionAttempt)).TotalSeconds);
                return false;
            }

            lock (_connectionLock)
            {
                // Double-check pattern
                if (_connectionInitialized && _ldapAvailable && _connection != null)
                {
                    return true;
                }

                _lastConnectionAttempt = DateTime.Now;
                
                // Tentative de connexion
                var task = InitializeLdapConnection();
                var result = task.GetAwaiter().GetResult();
                
                if (result)
                {
                    _logger.LogInformation("✅ Connexion LDAP rétablie avec succès");
                }
                else
                {
                    _logger.LogWarning("❌ Connexion LDAP échouée, retry dans {Minutes} minutes", _retryInterval.TotalMinutes);
                }
                
                return result;
            }
        }

        public bool UserExists(string userDn)
        {
            try
            {
                if (!EnsureConnectionAsync().GetAwaiter().GetResult())
                {
                    _logger.LogWarning("⚠️ UserExists: LDAP indisponible, retour false par défaut pour {UserDn}", userDn);
                    return false;
                }
                
                var req = new SearchRequest(userDn, "(objectClass=user)", SearchScope.Base);
                var res = (SearchResponse)_connection.SendRequest(req);
                return res.Entries.Count > 0;
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogDebug("Utilisateur {UserDn} non trouvé: {Message}", userDn, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la vérification d'existence de l'utilisateur {UserDn}", userDn);
                return false;
            }
        }

        public void CreateUser(string firstName, string lastName, string password, string ouDn)
        {
            if (!EnsureConnectionAsync().GetAwaiter().GetResult())
            {
                _logger.LogError("❌ CreateUser: LDAP indisponible, impossible de créer l'utilisateur {FirstName} {LastName}", firstName, lastName);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer l'utilisateur.");
            }
            
            try
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
                _logger.LogInformation("✅ User created {Dn}", dn);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la création de l'utilisateur {FirstName} {LastName}", firstName, lastName);
                throw;
            }
        }

        public void CreateOrganizationalUnit(string ouPath)
        {
            if (!EnsureConnectionAsync().GetAwaiter().GetResult())
            {
                _logger.LogError("❌ CreateOrganizationalUnit: LDAP indisponible, impossible de créer l'OU {OuPath}", ouPath);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer l'unité organisationnelle.");
            }
            
            try
            {
                var req = new AddRequest(ouPath, new DirectoryAttribute("objectClass", "organizationalUnit"));
                _connection.SendRequest(req);
                _logger.LogInformation("✅ OU created {OuPath}", ouPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la création de l'OU {OuPath}", ouPath);
                throw;
            }
        }

        public bool OrganizationalUnitExists(string ouDn)
        {
            try
            {
                if (!EnsureConnectionAsync().GetAwaiter().GetResult())
                {
                    _logger.LogWarning("⚠️ OrganizationalUnitExists: LDAP indisponible, retour false par défaut pour {OuDn}", ouDn);
                    return false;
                }
                
                var req = new SearchRequest(ouDn, "(objectClass=organizationalUnit)", SearchScope.Base);
                var res = (SearchResponse)_connection.SendRequest(req);
                bool exists = res.Entries.Count > 0;
                
                _logger.LogDebug("OU {OuDn} existe: {Exists}", ouDn, exists);
                return exists;
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogDebug("OU {OuDn} non trouvée: {Message}", ouDn, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la vérification d'existence de l'OU {OuDn}", ouDn);
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

        public async Task<bool> OrganizationalUnitExistsAsync(string ouPath)
        {
            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ OrganizationalUnitExistsAsync: LDAP indisponible, retour false par défaut pour {OuPath}", ouPath);
                    return false;
                }
                
                return OrganizationalUnitExists(ouPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la vérification async d'existence de l'OU {OuPath}", ouPath);
                return false;
            }
        }

        public async Task<bool> UserExistsAsync(string samAccountName)
        {
            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ UserExistsAsync: LDAP indisponible, retour false par défaut pour {SamAccountName}", samAccountName);
                    return false;
                }
                
                // S'assurer que _baseDn n'est pas null
                if (string.IsNullOrEmpty(_baseDn))
                {
                    _logger.LogWarning("Vérification d'existence d'utilisateur impossible: _baseDn est null pour {SamAccountName}", samAccountName);
                    return false;
                }
                
                var filter = $"(&(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
                var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, null);
                
                var res = (SearchResponse)_connection.SendRequest(req);
                bool exists = res.Entries.Count > 0;
                
                _logger.LogDebug("Utilisateur {SamAccountName} existe: {Exists}", samAccountName, exists);
                return exists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la vérification d'existence de l'utilisateur {SamAccountName}", samAccountName);
                return false;
            }
        }

     
        public UserModel CreateUser(string samAccountName, Dictionary<string, string> attributes, string ouDn)
        {
            if (!EnsureConnectionAsync().GetAwaiter().GetResult())
            {
                _logger.LogError("❌ CreateUser: LDAP indisponible, impossible de créer l'utilisateur {SamAccountName}", samAccountName);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer l'utilisateur.");
            }

            try
            {
                // ✅ Validation et préparation des attributs essentiels
                var (firstName, lastName, password) = ValidateAndExtractEssentialAttributes(attributes, samAccountName);
                
                // ✅ Construction du DN complet de l'utilisateur
                var cn = Escape(firstName + " " + lastName);
                var userDn = $"CN={cn},{ouDn}";
                
                // ✅ Préparation de tous les attributs AD mappés
                var directoryAttributes = PrepareAllAdAttributes(samAccountName, attributes, firstName, lastName);
                
                // ✅ Journalisation pour débogage
                _logger.LogInformation("🚀 Création utilisateur AD '{SamAccountName}' avec {AttributeCount} attributs mappés", 
                    samAccountName, directoryAttributes.Length);
                
                foreach (var attr in directoryAttributes)
                {
                    if (attr.Name != "userPassword" && attr.Name != "unicodePwd")
                    {
                        _logger.LogDebug("   📝 {AttributeName}: {Value}", attr.Name, 
                            attr.Count > 0 ? attr[0]?.ToString() : "null");
                    }
                }

                // ✅ Création effective de l'utilisateur avec tous les attributs
                _connection.SendRequest(new AddRequest(userDn, directoryAttributes));
                
                // ✅ Configuration du mot de passe et activation du compte
                SetPassword(userDn, password);
                EnableAccount(userDn);
                
                _logger.LogInformation("✅ Utilisateur AD créé avec succès: {UserDn}", userDn);

                // ✅ Construction du UserModel de retour
                return BuildUserModel(samAccountName, attributes, userDn, firstName, lastName, ouDn);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la création de l'utilisateur {SamAccountName}", samAccountName);
                throw;
            }
            finally
            {
                // 👈 NOUVEAU : Hook Teams après création réussie de l'utilisateur
                //OnUserCreatedAsync(samAccountName, ouDn).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// ✅ Valide et extrait les attributs essentiels (givenName, sn, password)
        /// </summary>
        private (string firstName, string lastName, string password) ValidateAndExtractEssentialAttributes(
            Dictionary<string, string> attributes, string samAccountName)
        {
            // Extraction du prénom
            attributes.TryGetValue("givenName", out var firstName);
            if (string.IsNullOrWhiteSpace(firstName))
            {
                // Tentative de dérivation depuis sAMAccountName
                var parts = samAccountName.Split('.');
                firstName = parts.Length >= 1 ? 
                    char.ToUpper(parts[0][0]) + parts[0].Substring(1).ToLower() : 
                    samAccountName;
                _logger.LogWarning("⚠️ givenName manquant, dérivé depuis sAMAccountName: '{FirstName}'", firstName);
            }

            // Extraction du nom de famille
            attributes.TryGetValue("sn", out var lastName);
            if (string.IsNullOrWhiteSpace(lastName))
            {
                var parts = samAccountName.Split('.');
                lastName = parts.Length >= 2 ? 
                    parts[1].ToUpper() : 
                    "USER";
                _logger.LogWarning("⚠️ sn manquant, dérivé depuis sAMAccountName: '{LastName}'", lastName);
            }

            // Extraction du mot de passe
            var password = attributes.GetValueOrDefault("password") ?? 
                          attributes.GetValueOrDefault("userPassword") ?? 
                          "Changeme1!";
            
            if (password == "Changeme1!")
            {
                _logger.LogWarning("⚠️ Mot de passe par défaut utilisé pour {SamAccountName}", samAccountName);
            }

            return (firstName, lastName, password);
        }

        /// <summary>
        /// ✅ Prépare tous les attributs Active Directory avec mapping intelligent
        /// </summary>
        private DirectoryAttribute[] PrepareAllAdAttributes(string samAccountName, 
            Dictionary<string, string> attributes, string firstName, string lastName)
        {
            var attrs = new List<DirectoryAttribute>();

            // ✅ Attributs obligatoires pour la création
            attrs.Add(new DirectoryAttribute("objectClass", new[] { "top", "person", "organizationalPerson", "user" }));
            attrs.Add(new DirectoryAttribute("cn", Escape(firstName + " " + lastName)));
            attrs.Add(new DirectoryAttribute("sAMAccountName", samAccountName));
            attrs.Add(new DirectoryAttribute("givenName", firstName));
            attrs.Add(new DirectoryAttribute("sn", lastName));

            // ✅ UserPrincipalName (requis pour la connexion)
            string upn = GenerateUserPrincipalName(samAccountName, attributes);
            attrs.Add(new DirectoryAttribute("userPrincipalName", upn));

            // ✅ Mapping de tous les autres attributs disponibles avec validation de longueur
            var additionalAttributes = new Dictionary<string, string>
            {
                ["displayName"] = attributes.GetValueOrDefault("displayName", $"{firstName} {lastName.ToUpper()}"),
                ["mail"] = attributes.GetValueOrDefault("mail"),
                ["title"] = attributes.GetValueOrDefault("title"),
                ["department"] = attributes.GetValueOrDefault("department"),
                ["division"] = attributes.GetValueOrDefault("division"),
                ["company"] = attributes.GetValueOrDefault("company"),
                ["description"] = attributes.GetValueOrDefault("description"),
                ["physicalDeliveryOfficeName"] = attributes.GetValueOrDefault("physicalDeliveryOfficeName"),
                ["initials"] = ValidateAndTruncateAttribute("initials", attributes.GetValueOrDefault("initials"), 6), // ✅ Limité à 6 caractères
                ["personalTitle"] = attributes.GetValueOrDefault("personalTitle"),
                ["pager"] = attributes.GetValueOrDefault("pager"),
                ["facsimileTelephoneNumber"] = attributes.GetValueOrDefault("facsimileTelephoneNumber"),
                ["homeDirectory"] = attributes.GetValueOrDefault("homeDirectory"),
                ["homeDrive"] = ValidateAndTruncateAttribute("homeDrive", attributes.GetValueOrDefault("homeDrive"), 3), // ✅ Format "C:"
                ["profilePath"] = attributes.GetValueOrDefault("profilePath"),
                ["scriptPath"] = attributes.GetValueOrDefault("scriptPath"),
                ["telephoneNumber"] = attributes.GetValueOrDefault("telephoneNumber"),
                ["mobile"] = attributes.GetValueOrDefault("mobile"),
                ["streetAddress"] = attributes.GetValueOrDefault("streetAddress"),
                ["l"] = attributes.GetValueOrDefault("l"), // Localité/Ville
                ["st"] = attributes.GetValueOrDefault("st"), // État/Province
                ["postalCode"] = ValidateAndTruncateAttribute("postalCode", attributes.GetValueOrDefault("postalCode"), 10), // ✅ Code postal
                ["co"] = attributes.GetValueOrDefault("co"), // Pays
                ["wWWHomePage"] = attributes.GetValueOrDefault("wWWHomePage"),
                ["info"] = attributes.GetValueOrDefault("info")
            };

            // ✅ Ajout des attributs non-vides à la liste
            foreach (var kvp in additionalAttributes)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                {
                    try
                    {
                        attrs.Add(new DirectoryAttribute(kvp.Key, kvp.Value.Trim()));
                        _logger.LogDebug("📝 Attribut ajouté: {AttributeName} = {Value}", kvp.Key, kvp.Value.Trim());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ Impossible d'ajouter l'attribut {AttributeName}: {Error}", kvp.Key, ex.Message);
                    }
                }
            }

            return attrs.ToArray();
        }
        
        /// <summary>
        /// ✅ Valide et tronque un attribut à la longueur maximale autorisée par AD
        /// </summary>
        private string ValidateAndTruncateAttribute(string attributeName, string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
                
            if (value.Length <= maxLength)
                return value;
                
            var truncated = value.Substring(0, maxLength);
            _logger.LogWarning("⚠️ Attribut {AttributeName} tronqué de {OriginalLength} à {MaxLength} caractères: '{Original}' -> '{Truncated}'", 
                attributeName, value.Length, maxLength, value, truncated);
                
            return truncated;
        }

        /// <summary>
        /// ✅ Génère un UserPrincipalName approprié
        /// </summary>
        private string GenerateUserPrincipalName(string samAccountName, Dictionary<string, string> attributes)
        {
            // Utiliser la valeur mappée si disponible
            if (attributes.TryGetValue("userPrincipalName", out var mappedUpn) && !string.IsNullOrWhiteSpace(mappedUpn))
            {
                return mappedUpn;
            }

            // Construire depuis le domaine
            string domainPart = "local";
            if (!string.IsNullOrEmpty(_baseDn))
            {
                domainPart = _baseDn.Replace(",DC=", ".").Replace("DC=", "");
            }

            return $"{samAccountName}@{domainPart}";
        }

        /// <summary>
        /// ✅ Construit le UserModel de retour avec tous les attributs
        /// </summary>
        private UserModel BuildUserModel(string samAccountName, Dictionary<string, string> attributes, 
            string distinguishedName, string firstName, string lastName, string ouDn)
        {
            string domainPart = "local";
            if (!string.IsNullOrEmpty(_baseDn))
            {
                domainPart = _baseDn.Replace("DC=", "").Replace(",", ".");
            }

            var additionalAttributes = new Dictionary<string, string>(attributes)
            {
                ["distinguishedName"] = distinguishedName
            };

            return new UserModel
            {
                SamAccountName = samAccountName,
                GivenName = firstName,
                Surname = lastName,
                DisplayName = attributes.GetValueOrDefault("displayName", $"{firstName} {lastName}"),
                UserPrincipalName = attributes.GetValueOrDefault("userPrincipalName", $"{samAccountName}@{domainPart}"),
                OrganizationalUnit = ouDn,
                AdditionalAttributes = additionalAttributes
            };
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

        /// <summary>
        /// Vérifie la santé du service LDAP sans tentative de reconnexion
        /// </summary>
        public bool IsLdapHealthy()
        {
            return _connectionInitialized && _ldapAvailable && _connection != null;
        }

        /// <summary>
        /// Obtient le statut détaillé de la connexion LDAP
        /// </summary>
        public LdapHealthStatus GetHealthStatus()
        {
            return new LdapHealthStatus
            {
                IsConnected = _connectionInitialized,
                IsAvailable = _ldapAvailable,
                LastConnectionAttempt = _lastConnectionAttempt,
                NextRetryTime = _lastConnectionAttempt.Add(_retryInterval),
                BaseDn = _baseDn,
                ConnectionEstablished = _connection != null
            };
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var context = await CreatePrincipalContextAsync();
                return context.ValidateCredentials(
                    await _ldapSettingsProvider.GetUsernameAsync(),
                    await _ldapSettingsProvider.GetPasswordAsync()
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du test de connexion LDAP");
                return false;
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

        public async Task<bool> DeleteOrganizationalUnitAsync(string ouDn, bool deleteIfNotEmpty = false)
        {
            _logger.LogInformation($"Demande de suppression de l'OU : {ouDn}. DeleteIfNotEmpty={deleteIfNotEmpty}");

            bool isEmpty = await IsOrganizationalUnitEmptyAsync(ouDn);

            if (isEmpty)
            {
                try
                {
                    var del = new DeleteRequest(ouDn);
                    _connection.SendRequest(del); // Note: SendRequest n'est pas async ici.
                    _logger.LogInformation("OU {Dn} supprimée car elle était vide.", ouDn);
                    return true;
                }
                catch (DirectoryOperationException ex)
                {
                    _logger.LogError(ex, $"Erreur DirectoryOperationException lors de la tentative de suppression de l'OU vide {ouDn}. Code: {ex.Response?.ResultCode}");
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Erreur inattendue lors de la tentative de suppression de l'OU vide {ouDn}.");
                    return false;
                }
            }
            else
            {
                if (deleteIfNotEmpty)
                {
                    _logger.LogWarning("L'OU {Dn} n'est pas vide mais deleteIfNotEmpty est true. La suppression récursive n'est PAS IMPLÉMENTÉE. L'OU N'A PAS ÉTÉ SUPPRIMÉE.", ouDn);
                    // Implémenter la suppression récursive ici si absolument nécessaire et avec une extrême prudence.
                    // throw new NotImplementedException("La suppression récursive d'OU non vide n'est pas implémentée.");
                    return false; 
                }
                else
                {
                    _logger.LogWarning("L'OU {Dn} n'a pas été supprimée car elle n'est pas vide et deleteIfNotEmpty est false.", ouDn);
                    // Pour correspondre à l'erreur originale et permettre à l'appelant de savoir que c'est parce qu'elle n'est pas vide :
                    // throw new DirectoryOperationException($"L'OU '{ouDn}' ne peut pas être supprimée car elle n'est pas vide (non-leaf object).", (int)DirectoryStatusCode.UnwillingToPerform); // Exemple de code d'erreur
                    return false; 
                }
            }
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

        private async Task<PrincipalContext> CreatePrincipalContextAsync()
        {
            try
            {
                var server = await _ldapSettingsProvider.GetServerAsync();
                var domain = await _ldapSettingsProvider.GetDomainAsync();
                var username = await _ldapSettingsProvider.GetUsernameAsync();
                var password = await _ldapSettingsProvider.GetPasswordAsync();

                return new PrincipalContext(
                    ContextType.Domain,
                    server,
                    username,
                    password
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la création du contexte principal");
                throw;
            }
        }

        // ✅ Implémentation des méthodes manquantes de l'interface ILdapService
        public async Task CreateOrganizationalUnitAsync(string ouPath)
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogError("❌ CreateOrganizationalUnitAsync: LDAP indisponible, impossible de créer l'OU {OuPath}", ouPath);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer l'unité organisationnelle.");
            }
            
            try
            {
                var req = new AddRequest(ouPath, new DirectoryAttribute("objectClass", "organizationalUnit"));
                _connection.SendRequest(req);
                _logger.LogInformation("✅ OU created async {OuPath}", ouPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la création async de l'OU {OuPath}", ouPath);
                throw;
            }
        }

        public async Task<UserModel?> GetUserAsync(string samAccountName)
        {
            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ GetUserAsync: LDAP indisponible, retour null pour {SamAccountName}", samAccountName);
                    return null;
                }
                
                if (string.IsNullOrEmpty(_baseDn))
                {
                    _logger.LogWarning("GetUserAsync impossible: _baseDn est null pour {SamAccountName}", samAccountName);
                    return null;
                }
                
                var filter = $"(&(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
                var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, new[] {
                    "cn", "distinguishedName", "givenName", "sn", "displayName", "userPrincipalName", 
                    "mail", "department", "title", "telephoneNumber"
                });
                
                var res = (SearchResponse)_connection.SendRequest(req);
                if (res.Entries.Count == 0)
                {
                    _logger.LogDebug("Utilisateur {SamAccountName} non trouvé", samAccountName);
                    return null;
                }
                
                var entry = res.Entries[0];
                var attributes = new Dictionary<string, string> { ["distinguishedName"] = entry.DistinguishedName };
                
                foreach (string attrName in entry.Attributes.AttributeNames)
                {
                    if (entry.Attributes[attrName].Count > 0)
                    {
                        attributes[attrName] = entry.Attributes[attrName][0].ToString() ?? string.Empty;
                    }
                }
                
                return new UserModel
                {
                    SamAccountName = samAccountName,
                    GivenName = attributes.GetValueOrDefault("givenName", ""),
                    Surname = attributes.GetValueOrDefault("sn", ""),
                    DisplayName = attributes.GetValueOrDefault("displayName", ""),
                    UserPrincipalName = attributes.GetValueOrDefault("userPrincipalName", ""),
                    OrganizationalUnit = ExtractOuFromDn(entry.DistinguishedName),
                    AdditionalAttributes = attributes
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération de l'utilisateur {SamAccountName}", samAccountName);
                return null;
            }
        }

        public async Task<List<UserModel>> GetAllUsersInOuAsync(string ouPath)
        {
            var users = new List<UserModel>();
            
            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ GetAllUsersInOuAsync: LDAP indisponible, retour liste vide pour {OuPath}", ouPath);
                    return users;
                }
                
                var filter = "(objectClass=user)";
                var req = new SearchRequest(ouPath, filter, SearchScope.OneLevel, new[] {
                    "sAMAccountName", "cn", "distinguishedName", "givenName", "sn", "displayName", "userPrincipalName", "mail"
                });
                
                var res = (SearchResponse)_connection.SendRequest(req);
                
                foreach (SearchResultEntry entry in res.Entries)
                {
                    var attributes = new Dictionary<string, string> { ["distinguishedName"] = entry.DistinguishedName };
                    
                    foreach (string attrName in entry.Attributes.AttributeNames)
                    {
                        if (entry.Attributes[attrName].Count > 0)
                        {
                            attributes[attrName] = entry.Attributes[attrName][0].ToString() ?? string.Empty;
                        }
                    }
                    
                    var samAccountName = attributes.GetValueOrDefault("sAMAccountName", "");
                    if (!string.IsNullOrEmpty(samAccountName))
                    {
                        users.Add(new UserModel
                        {
                            SamAccountName = samAccountName,
                            GivenName = attributes.GetValueOrDefault("givenName", ""),
                            Surname = attributes.GetValueOrDefault("sn", ""),
                            DisplayName = attributes.GetValueOrDefault("displayName", ""),
                            UserPrincipalName = attributes.GetValueOrDefault("userPrincipalName", ""),
                            OrganizationalUnit = ouPath,
                            AdditionalAttributes = attributes
                        });
                    }
                }
                
                _logger.LogInformation("✅ Récupéré {Count} utilisateurs depuis l'OU {OuPath}", users.Count, ouPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération des utilisateurs dans l'OU {OuPath}", ouPath);
            }
            
            return users;
        }

        public async Task CreateUserAsync(Dictionary<string, string> attributes, string ouPath)
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogError("❌ CreateUserAsync: LDAP indisponible, impossible de créer l'utilisateur");
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer l'utilisateur.");
            }
            
            var samAccountName = attributes.GetValueOrDefault("sAMAccountName", "");
            if (string.IsNullOrEmpty(samAccountName))
            {
                throw new ArgumentException("sAMAccountName est requis pour créer un utilisateur");
            }
            
            CreateUser(samAccountName, attributes, ouPath);
        }

        public async Task UpdateUserAsync(string samAccountName, Dictionary<string, string> attributes, string ouPath)
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogError("❌ UpdateUserAsync: LDAP indisponible, impossible de mettre à jour l'utilisateur {SamAccountName}", samAccountName);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de mettre à jour l'utilisateur.");
            }
            
            UpdateUser(samAccountName, attributes);
        }

        /// <summary>
        /// ✅ Méthode utilitaire pour extraire l'OU depuis un DN
        /// </summary>
        private string ExtractOuFromDn(string distinguishedName)
        {
            if (string.IsNullOrEmpty(distinguishedName)) return string.Empty;
            
            // Retirer la partie CN=username, pour ne garder que l'OU
            var parts = distinguishedName.Split(',');
            if (parts.Length > 1)
            {
                return string.Join(",", parts.Skip(1));
            }
            
            return distinguishedName;
        }

        /// <summary>
        /// ✅ NOUVELLE MÉTHODE : Récupère les attributs spécifiques d'un utilisateur depuis LDAP
        /// </summary>
        public async Task<Dictionary<string, string?>> GetUserAttributesAsync(string samAccountName, List<string> attributeNames)
        {
            var result = new Dictionary<string, string?>();
            
            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ GetUserAttributesAsync: LDAP indisponible pour {SamAccountName}", samAccountName);
                    return result;
                }
                
                if (string.IsNullOrEmpty(_baseDn))
                {
                    _logger.LogWarning("GetUserAttributesAsync impossible: _baseDn est null pour {SamAccountName}", samAccountName);
                    return result;
                }
                
                var filter = $"(&(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
                var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, attributeNames.ToArray());
                
                var res = (SearchResponse)_connection.SendRequest(req);
                if (res.Entries.Count == 0)
                {
                    _logger.LogDebug("Utilisateur {SamAccountName} non trouvé pour récupération d'attributs", samAccountName);
                    return result;
                }
                
                var entry = res.Entries[0];
                
                // Récupérer tous les attributs demandés
                foreach (var attrName in attributeNames)
                {
                    if (entry.Attributes.Contains(attrName) && entry.Attributes[attrName].Count > 0)
                    {
                        result[attrName] = entry.Attributes[attrName][0]?.ToString();
                    }
                    else
                    {
                        result[attrName] = null; // Attribut absent ou vide
                    }
                }
                
                _logger.LogDebug("✅ Récupéré {Count} attributs pour {SamAccountName}", result.Count, samAccountName);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération des attributs pour {SamAccountName}", samAccountName);
                return result;
            }
        }

        /// <summary>
        /// ✅ NOUVELLE MÉTHODE : Compare les attributs existants avec les nouveaux et met à jour uniquement si nécessaire
        /// </summary>
        public async Task<bool> CompareAndUpdateUserAsync(string samAccountName, Dictionary<string, string> newAttributes, string ouPath)
        {
            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogError("❌ CompareAndUpdateUserAsync: LDAP indisponible pour {SamAccountName}", samAccountName);
                    return false;
                }
                
                // 1. Récupérer les attributs existants depuis LDAP
                var attributesToCheck = newAttributes.Keys.ToList();
                var existingAttributes = await GetUserAttributesAsync(samAccountName, attributesToCheck);
                
                if (!existingAttributes.Any())
                {
                    _logger.LogWarning("⚠️ Impossible de récupérer les attributs existants pour {SamAccountName}", samAccountName);
                    return false;
                }
                
                // 2. Comparer les attributs et identifier les différences
                var attributesToUpdate = new Dictionary<string, string>();
                var comparisonLog = new List<string>();
                
                foreach (var newAttr in newAttributes)
                {
                    var attributeName = newAttr.Key;
                    var newValue = newAttr.Value?.Trim();
                    var existingValue = existingAttributes.GetValueOrDefault(attributeName)?.Trim();
                    
                    // Normaliser les valeurs nulles/vides
                    newValue = string.IsNullOrWhiteSpace(newValue) ? null : newValue;
                    existingValue = string.IsNullOrWhiteSpace(existingValue) ? null : existingValue;
                    
                    // Comparer les valeurs (insensible à la casse pour certains attributs)
                    bool isDifferent = !AreAttributeValuesEqual(attributeName, newValue, existingValue);
                    
                    if (isDifferent)
                    {
                        attributesToUpdate[attributeName] = newValue ?? string.Empty;
                        comparisonLog.Add($"   📝 {attributeName}: '{existingValue}' → '{newValue}'");
                    }
                    else
                    {
                        comparisonLog.Add($"   ✅ {attributeName}: inchangé ('{existingValue}')");
                    }
                }
                
                // 3. Logger les résultats de la comparaison
                _logger.LogInformation("🔍 Comparaison d'attributs pour {SamAccountName}:", samAccountName);
                foreach (var log in comparisonLog)
                {
                    _logger.LogInformation(log);
                }
                
                // 4. Mettre à jour uniquement si des différences ont été trouvées
                if (attributesToUpdate.Any())
                {
                    _logger.LogInformation("🚀 Mise à jour nécessaire pour {SamAccountName}: {Count} attributs modifiés", 
                        samAccountName, attributesToUpdate.Count);
                    
                    await UpdateUserAttributesAsync(samAccountName, attributesToUpdate);
                    
                    _logger.LogInformation("✅ Mise à jour réussie pour {SamAccountName}", samAccountName);
                    return true;
                }
                else
                {
                    _logger.LogInformation("⏭️ Aucune mise à jour nécessaire pour {SamAccountName}: tous les attributs sont identiques", samAccountName);
                    return false; // Pas de mise à jour nécessaire
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la comparaison/mise à jour pour {SamAccountName}", samAccountName);
                return false;
            }
        }

        /// <summary>
        /// ✅ MÉTHODE UTILITAIRE : Compare intelligemment deux valeurs d'attributs
        /// </summary>
        private bool AreAttributeValuesEqual(string attributeName, string? value1, string? value2)
        {
            // Attributs insensibles à la casse
            var caseInsensitiveAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mail", "userPrincipalName", "sAMAccountName", "cn", "distinguishedName"
            };
            
            // Attributs sensibles à la casse
            var caseSensitiveAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "description", "info", "title"
            };
            
            // Si les deux sont null ou vides, ils sont égaux
            if (string.IsNullOrWhiteSpace(value1) && string.IsNullOrWhiteSpace(value2))
                return true;
            
            // Si un seul est null/vide, ils sont différents
            if (string.IsNullOrWhiteSpace(value1) || string.IsNullOrWhiteSpace(value2))
                return false;
            
            // Comparaison selon le type d'attribut
            if (caseInsensitiveAttributes.Contains(attributeName))
            {
                return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);
            }
            else if (caseSensitiveAttributes.Contains(attributeName))
            {
                return string.Equals(value1, value2, StringComparison.Ordinal);
            }
            else
            {
                // Par défaut, comparaison insensible à la casse
                return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// ✅ MÉTHODE UTILITAIRE : Met à jour les attributs spécifiques d'un utilisateur
        /// </summary>
        private async Task UpdateUserAttributesAsync(string samAccountName, Dictionary<string, string> attributesToUpdate)
        {
            try
            {
                var userDn = GetUserDn(samAccountName);
                
                foreach (var attr in attributesToUpdate)
                {
                    var modifyRequest = new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, attr.Key, attr.Value);
                    _connection.SendRequest(modifyRequest);
                    
                    _logger.LogDebug("✅ Attribut {AttributeName} mis à jour pour {UserDn}: '{Value}'", 
                        attr.Key, userDn, attr.Value);
                }
                
                _logger.LogInformation("✅ Tous les attributs mis à jour avec succès pour {SamAccountName}", samAccountName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la mise à jour des attributs pour {SamAccountName}", samAccountName);
                throw;
            }
        }

        /// <summary>
        /// Crée un groupe de sécurité ou de distribution dans une OU Active Directory, avec description optionnelle
        /// </summary>
        /// <param name="groupName">Nom du groupe</param>
        /// <param name="ouDn">DistinguishedName de l'OU</param>
        /// <param name="isSecurity">true = groupe de sécurité, false = distribution</param>
        /// <param name="isGlobal">true = global, false = local</param>
        /// <param name="description">Description du groupe (optionnelle)</param>
        /// <param name="parentGroupDn">DN du groupe parent (optionnel, pour imbrication directe)</param>
        public void CreateGroup(string groupName, string ouDn, bool isSecurity = true, bool isGlobal = true, string? description = null, string? parentGroupDn = null)
        {
            if (!EnsureConnectionAsync().GetAwaiter().GetResult())
            {
                _logger.LogError("❌ CreateGroup: LDAP indisponible, impossible de créer le groupe {GroupName}", groupName);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer le groupe.");
            }

            try
            {
                var cn = Escape(groupName);
                var dn = $"CN={cn},{ouDn}";
                long groupType = 0;
                // https://learn.microsoft.com/fr-fr/windows/win32/adschema/a-grouptype
                if (isGlobal)
                    groupType |= 0x00000002; // Global
                else
                    groupType |= 0x00000004; // Domain Local
                if (isSecurity)
                    groupType |= 0x80000000; // Security Enabled
                // Sinon, distribution (pas de bit sécurité)

                var attrsList = new List<DirectoryAttribute>
                {
                    new DirectoryAttribute("objectClass", "group"),
                    new DirectoryAttribute("sAMAccountName", groupName),
                    new DirectoryAttribute("groupType", groupType.ToString()),
                    new DirectoryAttribute("cn", groupName)
                };
                if (!string.IsNullOrWhiteSpace(description))
                {
                    attrsList.Add(new DirectoryAttribute("description", description));
                }
                _connection.SendRequest(new AddRequest(dn, attrsList.ToArray()));
                _logger.LogInformation("✅ Groupe créé {Dn}", dn);

                // Ajout direct dans le groupe parent si demandé
                if (!string.IsNullOrWhiteSpace(parentGroupDn))
                {
                    AddGroupToGroup(dn, parentGroupDn);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la création du groupe {GroupName}", groupName);
                throw;
            }
        }

        /// <summary>
        /// Ajoute un utilisateur à un groupe AD (attribut member)
        /// </summary>
        /// <param name="userDn">DistinguishedName de l'utilisateur</param>
        /// <param name="groupDn">DistinguishedName du groupe</param>
        public void AddUserToGroup(string userDn, string groupDn)
        {
            if (!EnsureConnectionAsync().GetAwaiter().GetResult())
            {
                _logger.LogError("❌ AddUserToGroup: LDAP indisponible, impossible d'ajouter {UserDn} au groupe {GroupDn}", userDn, groupDn);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible d'ajouter l'utilisateur au groupe.");
            }
            try
            {
                var req = new ModifyRequest(groupDn, DirectoryAttributeOperation.Add, "member", userDn);
                _connection.SendRequest(req);
                _logger.LogInformation("✅ Utilisateur {UserDn} ajouté au groupe {GroupDn}", userDn, groupDn);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de l'ajout de l'utilisateur {UserDn} au groupe {GroupDn}", userDn, groupDn);
                throw;
            }
        }

        /// <summary>
        /// Ajoute un groupe comme membre d'un autre groupe AD (attribut member)
        /// </summary>
        /// <param name="childGroupDn">DN du groupe à ajouter comme membre</param>
        /// <param name="parentGroupDn">DN du groupe parent</param>
       /// Ajoute un DN (utilisateur OU groupe) dans un groupe parent.
        /// - Ignore silencieusement si le membre est déjà présent.
        /// - Lève une exception explicite pour les autres erreurs.
        private void AddMemberToGroup(string memberDn, string parentGroupDn)
        {
            if (!EnsureConnectionAsync().GetAwaiter().GetResult())
                throw new InvalidOperationException("LDAP indisponible.");

            try
            {
                // 1) Éviter l'erreur "EntryAlreadyExists"
                var existsReq = new SearchRequest(
                    parentGroupDn,
                    $"(&(objectClass=group)(member={Escape(memberDn)}))",
                    SearchScope.Base,
                    "cn");
                var existsRes = (SearchResponse)_connection.SendRequest(existsReq);
                if (existsRes.Entries.Count > 0)
                {
                    _logger.LogDebug("{Member} déjà membre de {Parent}, rien à faire.",
                                     memberDn, parentGroupDn);
                    return;
                }

                // 2) Ajout
                var mod = new ModifyRequest(parentGroupDn,
                                            DirectoryAttributeOperation.Add,
                                            "member",
                                            memberDn);
                _connection.SendRequest(mod);
                _logger.LogInformation("✅ {Member} ajouté à {Parent}", memberDn, parentGroupDn);
            }
            catch (DirectoryOperationException ex) when
                   (ex.Response?.ResultCode == ResultCode.EntryAlreadyExists)
            {
                // Course-condition : quelqu'un l'a ajouté juste avant nous
                _logger.LogDebug("{Member} déjà présent (race-condition) dans {Parent}", memberDn, parentGroupDn);
            }
            catch (DirectoryOperationException ex) when
                   (ex.Response?.ResultCode == ResultCode.ConstraintViolation)
            {
                _logger.LogError(ex,
                    "❌ Règle de nesting violée : impossible d'ajouter {Member} au groupe {Parent}. " +
                    "Vérifiez les types de groupes (Global, Universal, Domain Local) et le domaine.",
                    memberDn, parentGroupDn);
                throw;
            }
            catch (DirectoryOperationException ex) when
                   (ex.Response?.ResultCode == ResultCode.InsufficientAccessRights)
            {
                _logger.LogError(ex,
                    "❌ Accès refusé : le compte utilisé n'a pas l'autorisation Write Members sur {Parent}.",
                    parentGroupDn);
                throw;
            }
        }
        
        public void AddGroupToGroup(string childGroupDn, string parentGroupDn)
            => AddMemberToGroup(childGroupDn, parentGroupDn);
        
        /// <summary>
        /// Déplace un utilisateur d'une OU vers une autre sans AccountManagement
        /// (pure requête LDAP : ModifyDNRequest)
        /// </summary>
        public async Task MoveUserAsync(string samAccountName, string sourceOu, string targetOu)
        {
            if (string.IsNullOrWhiteSpace(samAccountName))
                throw new ArgumentException(nameof(samAccountName));
            if (string.IsNullOrWhiteSpace(sourceOu))
                throw new ArgumentException(nameof(sourceOu));
            if (string.IsNullOrWhiteSpace(targetOu))
                throw new ArgumentException(nameof(targetOu));

            if (!await EnsureConnectionAsync())
                throw new InvalidOperationException("Service LDAP indisponible");

            // DN complet de l'utilisateur
            var userDn = GetUserDn(samAccountName);

            // Vérification (optionnelle) que l'utilisateur est bien dans l'OU source
            if (!userDn.EndsWith("," + sourceOu, StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("⚠️ {Sam} n'est pas localisé dans l'OU source attendue ({SrcOu})",
                    samAccountName, sourceOu);

            // RDN = première partie du DN (" CN=John Doe ")
            var comma = userDn.IndexOf(',');
            if (comma < 0)
                throw new InvalidOperationException($"DN inattendu : {userDn}");
            var rdn = userDn.Substring(0, comma);

            // Requête LDAP " rename / move "
            var req = new ModifyDNRequest(userDn, targetOu, rdn);
            _connection.SendRequest(req);

            _logger.LogInformation("✅ Utilisateur {Sam} déplacé de {SrcOu} vers {DstOu}",
                samAccountName, sourceOu, targetOu);
        }


        /// <summary>
        /// Récupère l'OU actuelle d'un utilisateur
        /// </summary>
        /// <param name="samAccountName">Nom d'utilisateur</param>
        /// <returns>DN de l'OU actuelle ou null si utilisateur introuvable</returns>
        public async Task<string?> GetUserCurrentOuAsync(string samAccountName)
        {
            if (string.IsNullOrWhiteSpace(samAccountName))
                return null;

            if (!await EnsureConnectionAsync())
                return null;

            try
            {
                var dn = GetUserDn(samAccountName);
                return ExtractOuFromDistinguishedName(dn);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "❌ Impossible de déterminer l'OU courante pour {Sam}", samAccountName);
                return null;
            }
        }

        /// <summary>
        /// Extrait le chemin de l'OU depuis un DN complet
        /// Exemple: "CN=John Doe,OU=Users,OU=IT,DC=domain,DC=com" -> "OU=Users,OU=IT,DC=domain,DC=com"
        /// </summary>
        /// <param name="distinguishedName">DN complet</param>
        /// <returns>Chemin de l'OU</returns>
        private string ExtractOuFromDistinguishedName(string distinguishedName)
        {
            if (string.IsNullOrWhiteSpace(distinguishedName))
                return string.Empty;

            // Trouver la première occurrence d'OU= ou DC=
            var ouIndex = distinguishedName.IndexOf(",OU=", StringComparison.OrdinalIgnoreCase);
            var dcIndex = distinguishedName.IndexOf(",DC=", StringComparison.OrdinalIgnoreCase);
            
            int startIndex = -1;
            if (ouIndex >= 0 && dcIndex >= 0)
            {
                startIndex = Math.Min(ouIndex, dcIndex);
            }
            else if (ouIndex >= 0)
            {
                startIndex = ouIndex;
            }
            else if (dcIndex >= 0)
            {
                startIndex = dcIndex;
            }

            if (startIndex >= 0)
            {
                return distinguishedName.Substring(startIndex + 1); // +1 pour ignorer la virgule
            }

            return string.Empty;
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
    
    public class LdapHealthStatus
    {
        public bool IsConnected { get; set; }
        public bool IsAvailable { get; set; }
        public DateTime LastConnectionAttempt { get; set; }
        public DateTime NextRetryTime { get; set; }
        public string BaseDn { get; set; }
        public bool ConnectionEstablished { get; set; }
        public string Status => IsAvailable ? "✅ Disponible" : "❌ Indisponible";
        public string Message => IsAvailable 
            ? "Service LDAP opérationnel" 
            : $"Service LDAP indisponible. Prochain essai: {NextRetryTime:HH:mm:ss}";
    }
}