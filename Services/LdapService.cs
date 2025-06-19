using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using System.Text;
using ADManagerAPI.Config;
using ADManagerAPI.Controllers;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using SearchScope = System.DirectoryServices.Protocols.SearchScope;

namespace ADManagerAPI.Services;

public partial class LdapService : ILdapService, IDisposable
{
    private readonly object _connectionLock = new();
    private readonly LdapSettingsProvider _ldapSettingsProvider;
    private readonly ILogger<LdapService> _logger;
    private readonly TimeSpan _retryInterval = TimeSpan.FromMinutes(5); // Retry chaque 5 minutes
    public string _baseDn = string.Empty;
    public LdapConnection? _connection;
    private bool _connectionInitialized;
    private DateTime _lastConnectionAttempt = DateTime.MinValue;
    private bool _ldapAvailable = true;

    public LdapService(ILogger<LdapService> logger, LdapSettingsProvider ldapSettingsProvider) // 👈 NOUVEAU
    {
        _logger = logger;
        _ldapSettingsProvider = ldapSettingsProvider;
        _logger.LogInformation("LdapService initialisé avec lazy loading. Connexion établie au premier appel.");
    }

    public void CreateOrganizationalUnit(string ouPath)
    {
        if (!EnsureConnectionAsync().GetAwaiter().GetResult())
        {
            _logger.LogError("❌ CreateOrganizationalUnit: LDAP indisponible, impossible de créer l'OU {OuPath}",
                ouPath);
            throw new InvalidOperationException(
                "Service LDAP indisponible. Impossible de créer l'unité organisationnelle.");
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

    public async Task<bool> OrganizationalUnitExistsAsync(string ouPath)
    {
        try
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogWarning(
                    "⚠️ OrganizationalUnitExistsAsync: LDAP indisponible, retour false par défaut pour {OuPath}",
                    ouPath);
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
                _logger.LogWarning(
                    "⚠️ UserExistsAsync: LDAP indisponible, retour false par défaut pour {SamAccountName}",
                    samAccountName);
                return false;
            }

            // S'assurer que _baseDn n'est pas null
            if (string.IsNullOrEmpty(_baseDn))
            {
                _logger.LogWarning(
                    "Vérification d'existence d'utilisateur impossible: _baseDn est null pour {SamAccountName}",
                    samAccountName);
                return false;
            }

            var filter = $"(&(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
            var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, null);

            var res = (SearchResponse)_connection.SendRequest(req);
            var exists = res.Entries.Count > 0;

            _logger.LogDebug("Utilisateur {SamAccountName} existe: {Exists}", samAccountName, exists);
            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la vérification d'existence de l'utilisateur {SamAccountName}",
                samAccountName);
            return false;
        }
    }


    public async Task<UserModel> CreateUser(string samAccountName, Dictionary<string, string> attributes, string ouDn)
    {
        if (!EnsureConnectionAsync().GetAwaiter().GetResult())
        {
            _logger.LogError("❌ CreateUser: LDAP indisponible, impossible de créer l'utilisateur {SamAccountName}",
                samAccountName);
            throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer l'utilisateur.");
        }

        try
        {
            // ✅ Validation et préparation des attributs essentiels
            var (firstName, lastName, password) = ValidateAndExtractEssentialAttributes(attributes, samAccountName);

            // ✅ Construction du DN complet de l'utilisateur
            var cn = Escape(firstName + " " + lastName);
            var userDn = $"CN={cn},{ouDn}";

            // ✅ Préparation de tous les attributs AD mappés (comme avant, mais avec compte désactivé)
            var directoryAttributes = PrepareAllAdAttributes(samAccountName, attributes, firstName, lastName);

            // ✅ Journalisation pour débogage
            _logger.LogInformation(
                "🚀 Création utilisateur AD '{SamAccountName}' avec {AttributeCount} attributs mappés",
                samAccountName, directoryAttributes.Length);

            foreach (var attr in directoryAttributes)
                if (attr.Name != "userPassword" && attr.Name != "unicodePwd")
                    _logger.LogDebug("   📝 {AttributeName}: {Value}", attr.Name,
                        attr.Count > 0 ? attr[0]?.ToString() : "null");

            // ✅ Tentative de création avec LDAP Connection
            bool userCreatedViaFallback = false;
            try
            {
                _connection.SendRequest(new AddRequest(userDn, directoryAttributes));
                _logger.LogInformation("✅ Utilisateur créé avec LdapConnection: {UserDn}", userDn);
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogWarning(ex, "⚠️ Impossible de créer l'utilisateur via LDAP (DirectoryOperationException) pour {UserDn}. " +
                                   "Code: {ResultCode}, Message: {Message}. Tentative avec DirectoryEntry...", 
                                   userDn, ex.Response?.ResultCode, ex.Message);
                
                // ✅ FALLBACK : Utiliser DirectoryEntry qui est plus compatible et propre
                await CreateUserWithDirectoryEntry(samAccountName, attributes, ouDn, firstName, lastName, password);
                userCreatedViaFallback = true;
            }
            
            // ✅ Définir le mot de passe et activer le compte si créé via LDAP Connection
            if (!userCreatedViaFallback)
            {
                SetPassword(userDn, password);
                _logger.LogInformation("✅ Mot de passe défini avec succès pour {UserDn}", userDn);
                
                // ✅ Activer le compte maintenant que le mot de passe est défini
                EnableAccount(userDn);
                _logger.LogInformation("✅ Compte activé avec succès pour {UserDn}", userDn);
            }

            _logger.LogInformation("✅ Utilisateur AD créé avec succès: {UserDn}", userDn);

            // ✅ Construction du UserModel de retour
            return BuildUserModel(samAccountName, attributes, userDn, firstName, lastName, ouDn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la création de l'utilisateur {SamAccountName}", samAccountName);
            throw;
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

        try
        {
            _logger.LogInformation("🔍 Récupération de toutes les unités organisationnelles");

            // ✅ FIX CRITIQUE : S'assurer que la connexion est établie avant d'utiliser _connection et _baseDn
            if (!await EnsureConnectionAsync())
            {
                _logger.LogWarning("⚠️ GetAllOrganizationalUnitsAsync: LDAP indisponible, retour liste vide");
                return list;
            }

            // ✅ FIX CRITIQUE : Vérifier que _baseDn n'est pas null
            if (string.IsNullOrEmpty(_baseDn))
            {
                _logger.LogWarning("⚠️ GetAllOrganizationalUnitsAsync: _baseDn est null, retour liste vide");
                return list;
            }

            // ✅ FIX CRITIQUE : Vérifier que _connection n'est pas null
            if (_connection == null)
            {
                _logger.LogWarning("⚠️ GetAllOrganizationalUnitsAsync: _connection est null, retour liste vide");
                return list;
            }

            // Requête LDAP sécurisée
            var req = new SearchRequest(_baseDn, "(objectClass=organizationalUnit)", SearchScope.Subtree, "ou",
                "distinguishedName");
            var res = (SearchResponse)_connection.SendRequest(req);

            if (res == null)
            {
                return list;
            }

            // Traitement des résultats avec protection contre null
            foreach (SearchResultEntry e in res.Entries)
            {
                if (e == null) continue;

                try
                {
                    var ou = new OrganizationalUnitModel
                    {
                        DistinguishedName = e.DistinguishedName ?? string.Empty,
                        Name = GetOuNameFromEntry(e)
                    };

                    list.Add(ou);
                }
                catch (Exception entryEx)
                {
                    _logger.LogWarning(entryEx, "⚠️ Erreur lors du traitement d'une entrée OU: {DN}",
                        e.DistinguishedName);
                }
            }

            _logger.LogInformation("✅ {Count} unités organisationnelles récupérées avec succès", list.Count);
        }
        catch (DirectoryOperationException ex)
        {
            _logger.LogError(ex, "❌ Erreur LDAP lors de la récupération des OUs: {Message} (Code: {Code})",
                ex.Message, ex.Response?.ResultCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur générale lors de la récupération des unités organisationnelles");
        }

        return list;
    }


    /// <summary>
    ///     Vérifie la santé du service LDAP sans tentative de reconnexion
    /// </summary>
    public bool IsLdapHealthy()
    {
        return _connectionInitialized && _ldapAvailable && _connection != null;
    }

    /// <summary>
    ///     Obtient le statut détaillé de la connexion LDAP
    /// </summary>
    public LdapHealthStatus GetHealthStatus()
    {
        return new LdapHealthStatus
        {
            IsConnected = _connectionInitialized,
            IsAvailable = _ldapAvailable,
            LastConnectionAttempt = _lastConnectionAttempt,
            NextRetryTime = _lastConnectionAttempt.Add(_retryInterval),
            BaseDn = string.IsNullOrEmpty(_baseDn) ? "Non configuré" : _baseDn,
            ConnectionEstablished = _connection != null
        };
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            // S'assurer que la connexion LDAP est initialisée
            var connectionOk = await EnsureConnectionAsync();
            if (!connectionOk)
                return false;

            // Test avec PrincipalContext pour valider les credentials
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
        var req = new SearchRequest(ouDn, "(objectClass=user)", SearchScope.Subtree, "sAMAccountName");
        var res = (SearchResponse)_connection.SendRequest(req);
        foreach (SearchResultEntry e in res.Entries)
            list.Add(e.Attributes["sAMAccountName"][0].ToString());
        return list;
    }

    public void DeleteUser(string samAccountName, string currentOuDn)
    {
        try
        {
            // ✅ CORRECTION: Vérifier et établir la connexion LDAP avant toute opération
            if (!EnsureConnectionAsync().GetAwaiter().GetResult())
            {
                _logger.LogError("❌ Impossible d'établir une connexion LDAP pour supprimer {Sam}", samAccountName);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de supprimer l'utilisateur.");
            }
            
            // Vérifier si _baseDn est null
            if (string.IsNullOrEmpty(_baseDn))
            {
                // Si nous avons l'OU courante, nous pouvons construire le DN de l'utilisateur
                if (!string.IsNullOrEmpty(currentOuDn))
                {
                    var firstName = string.Empty;
                    var lastName = string.Empty;

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
                            for (var i = 2; i < parts.Length; i++)
                                lastName += " " + parts[i];

                        // Construire un DN basé sur le format courant
                        var cn = $"{firstName} {lastName}";
                        var dn = $"CN={cn},{currentOuDn}";

                        try
                        {
                            var del = new DeleteRequest(dn);
                            _connection.SendRequest(del);
                            _logger.LogInformation("User deleted using constructed DN {Dn}", dn);
                            return;
                        }
                        catch (DirectoryOperationException ex)
                        {
                            _logger.LogWarning(ex,
                                "Échec de la suppression avec le DN construit {Dn}. L'utilisateur n'existe probablement pas.",
                                dn);
                        }
                    }
                }

                _logger.LogWarning("BaseDn non configuré et impossible de construire un DN valide pour {Sam}",
                    samAccountName);
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

        var isEmpty = await IsOrganizationalUnitEmptyAsync(ouDn);

        if (isEmpty)
            try
            {
                var del = new DeleteRequest(ouDn);
                _connection.SendRequest(del); // Note: SendRequest n'est pas async ici.
                _logger.LogInformation("OU {Dn} supprimée car elle était vide.", ouDn);
                return true;
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogError(ex,
                    $"Erreur DirectoryOperationException lors de la tentative de suppression de l'OU vide {ouDn}. Code: {ex.Response?.ResultCode}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur inattendue lors de la tentative de suppression de l'OU vide {ouDn}.");
                return false;
            }

        if (deleteIfNotEmpty)
        {
            _logger.LogWarning(
                "L'OU {Dn} n'est pas vide mais deleteIfNotEmpty est true. La suppression récursive n'est PAS IMPLÉMENTÉE. L'OU N'A PAS ÉTÉ SUPPRIMÉE.",
                ouDn);
            // Implémenter la suppression récursive ici si absolument nécessaire et avec une extrême prudence.
            // throw new NotImplementedException("La suppression récursive d'OU non vide n'est pas implémentée.");
            return false;
        }

        _logger.LogWarning("L'OU {Dn} n'a pas été supprimée car elle n'est pas vide et deleteIfNotEmpty est false.",
            ouDn);
        // Pour correspondre à l'erreur originale et permettre à l'appelant de savoir que c'est parce qu'elle n'est pas vide :
        // throw new DirectoryOperationException($"L'OU '{ouDn}' ne peut pas être supprimée car elle n'est pas vide (non-leaf object).", (int)DirectoryStatusCode.UnwillingToPerform); // Exemple de code d'erreur
        return false;
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

    /// <summary>
    /// Vérifie si une OU est vide d'utilisateurs (peut contenir des groupes ou autres objets)
    /// </summary>
    public async Task<bool> IsOrganizationalUnitEmptyOfUsersAsync(string ouDn)
    {
        try
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogWarning("⚠️ IsOrganizationalUnitEmptyOfUsersAsync: LDAP indisponible, retour true pour {OuDn}", ouDn);
                return true;
            }

            // ✅ CORRECTION : Utiliser le bon filtre LDAP pour les utilisateurs dans Active Directory
            // objectCategory=person ET objectClass=user pour être sûr d'avoir les vrais utilisateurs
            var req = new SearchRequest(ouDn, "(&(objectCategory=person)(objectClass=user))", SearchScope.OneLevel, "distinguishedName");
            var res = (SearchResponse)_connection.SendRequest(req);
            
            var hasUsers = res.Entries.Count > 0;
            _logger.LogDebug("🔍 OU {OuDn} contient {UserCount} utilisateur(s) réel(s)", ouDn, res.Entries.Count);
            
            return !hasUsers; // Retourne true si pas d'utilisateurs
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la vérification des utilisateurs dans OU {OuDn}", ouDn);
            return false;
        }
    }

    public void DeleteOrganizationalUnit(string ouDn)
    {
        var del = new DeleteRequest(ouDn);
        _connection.SendRequest(del);
        _logger.LogInformation("OU deleted {Dn}", ouDn);
    }

    /// <summary>
    /// Vérifie si un groupe est vide (sans membres)
    /// </summary>
    public async Task<bool> IsGroupEmptyAsync(string groupDn)
    {
        try
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogWarning("⚠️ IsGroupEmptyAsync: LDAP indisponible, retour true par défaut pour {GroupDn}", groupDn);
                return true;
            }

            var req = new SearchRequest(groupDn, "(objectClass=*)", SearchScope.Base, "member");
            var res = (SearchResponse)_connection.SendRequest(req);
            
            if (res.Entries.Count == 0)
            {
                _logger.LogDebug("Groupe {GroupDn} non trouvé, considéré comme vide", groupDn);
                return true;
            }

            var groupEntry = res.Entries[0];
            var memberAttribute = groupEntry.Attributes["member"];
            var memberCount = memberAttribute?.Count ?? 0;
            
            _logger.LogDebug("Groupe {GroupDn} contient {MemberCount} membres", groupDn, memberCount);
            return memberCount == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la vérification si le groupe {GroupDn} est vide", groupDn);
            return false;
        }
    }

    /// <summary>
    /// Supprime un groupe de sécurité ou de distribution
    /// </summary>
    public async Task DeleteGroupAsync(string groupDn)
    {
        try
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogError("❌ DeleteGroupAsync: LDAP indisponible, impossible de supprimer le groupe {GroupDn}", groupDn);
                throw new InvalidOperationException("Service LDAP indisponible. Impossible de supprimer le groupe.");
            }

            var del = new DeleteRequest(groupDn);
            _connection.SendRequest(del);
            _logger.LogInformation("✅ Groupe supprimé {GroupDn}", groupDn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la suppression du groupe {GroupDn}", groupDn);
            throw;
        }
    }

    /// <summary>
    /// Récupère tous les groupes dans une OU donnée
    /// </summary>
    public async Task<List<string>> GetGroupsInOUAsync(string ouDn)
    {
        var groups = new List<string>();
        
        try
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogWarning("⚠️ GetGroupsInOUAsync: LDAP indisponible, retour liste vide pour {OuDn}", ouDn);
                return groups;
            }

            var req = new SearchRequest(ouDn, "(objectClass=group)", SearchScope.OneLevel, "distinguishedName", "cn");
            var res = (SearchResponse)_connection.SendRequest(req);
            
            foreach (SearchResultEntry entry in res.Entries)
            {
                groups.Add(entry.DistinguishedName);
            }
            
            _logger.LogDebug("Trouvé {GroupCount} groupes dans OU {OuDn}", groups.Count, ouDn);
            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des groupes dans OU {OuDn}", ouDn);
            return groups;
        }
    }

    // ✅ Implémentation des méthodes manquantes de l'interface ILdapService
    public async Task CreateOrganizationalUnitAsync(string ouPath)
    {
        if (!await EnsureConnectionAsync())
        {
            _logger.LogError("❌ CreateOrganizationalUnitAsync: LDAP indisponible, impossible de créer l'OU {OuPath}",
                ouPath);
            throw new InvalidOperationException(
                "Service LDAP indisponible. Impossible de créer l'unité organisationnelle.");
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
                _logger.LogWarning("⚠️ GetUserAsync: LDAP indisponible, retour null pour {SamAccountName}",
                    samAccountName);
                return null;
            }

            if (string.IsNullOrEmpty(_baseDn))
            {
                _logger.LogWarning("GetUserAsync impossible: _baseDn est null pour {SamAccountName}", samAccountName);
                return null;
            }

            var filter = $"(&(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
            var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, "cn", "distinguishedName", "givenName",
                "sn", "displayName", "userPrincipalName", "mail", "department", "title", "telephoneNumber");

            var res = (SearchResponse)_connection.SendRequest(req);
            if (res.Entries.Count == 0)
            {
                _logger.LogDebug("Utilisateur {SamAccountName} non trouvé", samAccountName);
                return null;
            }

            var entry = res.Entries[0];
            var attributes = new Dictionary<string, string> { ["distinguishedName"] = entry.DistinguishedName };

            foreach (string attrName in entry.Attributes.AttributeNames)
                if (entry.Attributes[attrName].Count > 0)
                    attributes[attrName] = entry.Attributes[attrName][0].ToString() ?? string.Empty;

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
                _logger.LogWarning("⚠️ GetAllUsersInOuAsync: LDAP indisponible, retour liste vide pour {OuPath}",
                    ouPath);
                return users;
            }

            var filter = "(objectClass=user)";
            var req = new SearchRequest(ouPath, filter, SearchScope.Subtree, "sAMAccountName", "cn",
                "distinguishedName", "givenName", "sn", "displayName", "userPrincipalName", "mail");

            var res = (SearchResponse)_connection.SendRequest(req);

            foreach (SearchResultEntry entry in res.Entries)
            {
                var attributes = new Dictionary<string, string> { ["distinguishedName"] = entry.DistinguishedName };

                foreach (string attrName in entry.Attributes.AttributeNames)
                    if (entry.Attributes[attrName].Count > 0)
                        attributes[attrName] = entry.Attributes[attrName][0].ToString() ?? string.Empty;

                var samAccountName = attributes.GetValueOrDefault("sAMAccountName", "");
                if (!string.IsNullOrEmpty(samAccountName))
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

            _logger.LogInformation("✅ Récupéré {Count} utilisateurs depuis l'OU {OuPath} (recherche récursive)", users.Count, ouPath);
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des utilisateurs dans l'OU {OuPath}", ouPath);
        }

        return users;
    }

    public async Task CreateUserAsync(Dictionary<string, string> attributes, string ouPath, string? defaultPassword = null)
    {
        if (!await EnsureConnectionAsync())
        {
            _logger.LogError("❌ CreateUserAsync: LDAP indisponible, impossible de créer l'utilisateur");
            throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer l'utilisateur.");
        }

        var samAccountName = attributes.GetValueOrDefault("sAMAccountName", "");
        if (string.IsNullOrEmpty(samAccountName))
            throw new ArgumentException("sAMAccountName est requis pour créer un utilisateur");

        // ✅ Utiliser le mot de passe par défaut si fourni et si password n'est pas dans les attributs
        if (!string.IsNullOrWhiteSpace(defaultPassword) && 
            !attributes.ContainsKey("password") && 
            !attributes.ContainsKey("userPassword"))
        {
            attributes = new Dictionary<string, string>(attributes)
            {
                ["password"] = defaultPassword
            };
            _logger.LogInformation("🔑 Utilisation du mot de passe par défaut pour {SamAccountName}", samAccountName);
        }

        await CreateUser(samAccountName, attributes, ouPath);
    }

    public async Task UpdateUserAsync(string samAccountName, Dictionary<string, string> attributes, string ouPath)
    {
        if (!await EnsureConnectionAsync())
        {
            _logger.LogError(
                "❌ UpdateUserAsync: LDAP indisponible, impossible de mettre à jour l'utilisateur {SamAccountName}",
                samAccountName);
            throw new InvalidOperationException(
                "Service LDAP indisponible. Impossible de mettre à jour l'utilisateur.");
        }

        UpdateUser(samAccountName, attributes);
    }

    /// <summary>
    ///     ✅ NOUVELLE MÉTHODE : Récupère les attributs spécifiques d'un utilisateur depuis LDAP
    /// </summary>
    public async Task<Dictionary<string, string?>> GetUserAttributesAsync(string samAccountName,
        List<string> attributeNames)
    {
        var result = new Dictionary<string, string?>();

        try
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogWarning("⚠️ GetUserAttributesAsync: LDAP indisponible pour {SamAccountName}",
                    samAccountName);
                return result;
            }

            if (string.IsNullOrEmpty(_baseDn))
            {
                _logger.LogWarning("GetUserAttributesAsync impossible: _baseDn est null pour {SamAccountName}",
                    samAccountName);
                return result;
            }

            _logger.LogError("🔍 [ANALYSE LDAP] Recherche des attributs pour {SamAccountName}: {AttributeNames}",
                samAccountName, string.Join(", ", attributeNames));

            var filter = $"(&(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
            // ✅ MODIFICATION CRITIQUE : Récupérer TOUS les attributs au lieu de seulement ceux demandés
            var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, "*");

            var res = (SearchResponse)_connection.SendRequest(req);
            if (res.Entries.Count == 0)
            {
                _logger.LogWarning("❌ Utilisateur {SamAccountName} non trouvé pour récupération d'attributs",
                    samAccountName);
                return result;
            }

            var entry = res.Entries[0];
            _logger.LogWarning("✅ [DEBUG] Utilisateur {SamAccountName} trouvé, DN: {DistinguishedName}",
                samAccountName, entry.DistinguishedName);

            // Logger tous les attributs disponibles sur l'entrée LDAP
            var availableAttributes = entry.Attributes.AttributeNames.Cast<string>().OrderBy(x => x).ToList();
            _logger.LogError("📋 [ANALYSE LDAP] Attributs disponibles dans l'AD pour {SamAccountName} ({Count} total): {AvailableAttributes}",
                samAccountName, availableAttributes.Count, string.Join(", ", availableAttributes));

            // Récupérer tous les attributs demandés
            foreach (var attrName in attributeNames)
            {
                if (entry.Attributes.Contains(attrName) && entry.Attributes[attrName].Count > 0)
                {
                    var value = entry.Attributes[attrName][0]?.ToString();
                    result[attrName] = value;
                    _logger.LogWarning("✅ [DEBUG] Attribut {AttributeName} récupéré: '{Value}'", attrName, value);
                }
                else
                {
                    result[attrName] = null; // Attribut absent ou vide
                    _logger.LogWarning("⚠️ [DEBUG] Attribut {AttributeName} absent ou vide dans l'AD", attrName);
                    
                    // Chercher des attributs similaires pour suggestion
                    var similarAttributes = availableAttributes
                        .Where(a => a.Contains(attrName, StringComparison.OrdinalIgnoreCase) || 
                                   attrName.Contains(a, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    
                    if (similarAttributes.Any())
                    {
                        _logger.LogWarning("💡 [DEBUG] Attributs similaires trouvés pour {AttributeName}: {SimilarAttributes}",
                            attrName, string.Join(", ", similarAttributes));
                    }
                }
            }

            _logger.LogWarning("✅ [DEBUG] Récupéré {Count} attributs demandés sur {Total} pour {SamAccountName}", 
                result.Count(x => x.Value != null), attributeNames.Count, samAccountName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des attributs pour {SamAccountName}", samAccountName);
            return result;
        }
    }

    /// <summary>
    ///     Compare les attributs existants avec les nouveaux et met à jour uniquement si nécessaire
    /// </summary>
    public async Task<bool> CompareAndUpdateUserAsync(string samAccountName, Dictionary<string, string> newAttributes,
        string ouPath)
    {
        try
        {
            _logger.LogError("🚨 [EXECUTION DEBUG] CompareAndUpdateUserAsync APPELÉE pour {SamAccountName} avec {Count} attributs", 
                samAccountName, newAttributes.Count);
            
            if (!await EnsureConnectionAsync())
            {
                _logger.LogError("❌ CompareAndUpdateUserAsync: LDAP indisponible pour {SamAccountName}",
                    samAccountName);
                return false;
            }

            // ✅ CORRECTION : Filtrer les attributs pour la comparaison (même logique que l'analyse)
            var attributesForComparison = PrepareAttributesForComparison(newAttributes);
            
            if (!attributesForComparison.Any())
            {
                _logger.LogInformation(
                    "⏭️ Aucun attribut comparable pour {SamAccountName} - pas de mise à jour nécessaire",
                    samAccountName);
                return false;
            }

            _logger.LogDebug("🔍 Attributs sélectionnés pour comparaison de {SamAccountName}: {AttributeNames}",
                samAccountName, string.Join(", ", attributesForComparison.Keys));

            // 1. Récupérer les attributs existants depuis LDAP
            var attributesToCheck = attributesForComparison.Keys.ToList();

            var existingAttributes = await GetUserAttributesAsync(samAccountName, attributesToCheck);

            if (!existingAttributes.Any())
            {
                _logger.LogWarning("⚠️ Impossible de récupérer les attributs existants pour {SamAccountName}",
                    samAccountName);
                return false;
            }

            // 2. Comparer les attributs et identifier les différences
            var attributesToUpdate = new Dictionary<string, string>();
            var comparisonLog = new List<string>();

            foreach (var newAttr in attributesForComparison)
            {
                var attributeName = newAttr.Key;
                var newValue = newAttr.Value?.Trim();
                var existingValue = existingAttributes.GetValueOrDefault(attributeName)?.Trim();

                // Normaliser les valeurs nulles/vides
                newValue = string.IsNullOrWhiteSpace(newValue) ? null : newValue;
                existingValue = string.IsNullOrWhiteSpace(existingValue) ? null : existingValue;

                // Comparer les valeurs (insensible à la casse pour certains attributs)
                var isDifferent = !AreAttributeValuesEqual(attributeName, newValue, existingValue);

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
            foreach (var log in comparisonLog) _logger.LogInformation(log);

            // 4. Mettre à jour uniquement si des différences ont été trouvées
            if (attributesToUpdate.Any())
            {
                _logger.LogInformation("🚀 Mise à jour nécessaire pour {SamAccountName}: {Count} attributs modifiés",
                    samAccountName, attributesToUpdate.Count);

                await UpdateUserAttributesAsync(samAccountName, attributesToUpdate);

                _logger.LogInformation("✅ Mise à jour réussie pour {SamAccountName}", samAccountName);
                return true;
            }

            _logger.LogInformation(
                "⏭️ Aucune mise à jour nécessaire pour {SamAccountName}: tous les attributs sont identiques",
                samAccountName);
            return false; // Pas de mise à jour nécessaire
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la comparaison/mise à jour pour {SamAccountName}", samAccountName);
            return false;
        }
    }

    /// <summary>
    ///     ✅ MÉTHODE UTILITAIRE : Prépare les attributs pour la comparaison (même logique que l'analyse)
    /// </summary>
    private Dictionary<string, string> PrepareAttributesForComparison(Dictionary<string, string> allAttributes)
    {
        // Attributs à exclure de la comparaison (système, calculés ou non modifiables)
        var excludedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "password", "userPassword", "unicodePwd",
            "objectClass", "objectGuid", "objectSid",
            "whenCreated", "whenChanged", "lastLogon",
            "distinguishedName", "cn", "sAMAccountName" // CN et sAMAccountName ne changent généralement pas
        };

        var result = new Dictionary<string, string>();

        foreach (var kvp in allAttributes)
            if (!excludedAttributes.Contains(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                result[kvp.Key] = kvp.Value.Trim();

        return result;
    }

    /// <summary>
    ///     Crée un groupe de sécurité ou de distribution dans une OU Active Directory, avec description optionnelle
    /// </summary>
    /// <param name="groupName">Nom du groupe</param>
    /// <param name="ouDn">DistinguishedName de l'OU</param>
    /// <param name="isSecurity">true = groupe de sécurité, false = distribution</param>
    /// <param name="isGlobal">true = global, false = local</param>
    /// <param name="description">Description du groupe (optionnelle)</param>
    /// <param name="parentGroupDn">DN du groupe parent (optionnel, pour imbrication directe)</param>
    public void CreateGroup(string groupName, string ouDn, bool isSecurity = true, bool isGlobal = true,
        string? description = null, string? parentGroupDn = null)
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
                new("objectClass", "group"),
                new("sAMAccountName", groupName),
                new("groupType", groupType.ToString()),
                new("cn", groupName)
            };
            if (!string.IsNullOrWhiteSpace(description))
                attrsList.Add(new DirectoryAttribute("description", description));
            _connection.SendRequest(new AddRequest(dn, attrsList.ToArray()));
            _logger.LogInformation("✅ Groupe créé {Dn}", dn);

            // Ajout direct dans le groupe parent si demandé
            if (!string.IsNullOrWhiteSpace(parentGroupDn)) AddGroupToGroup(dn, parentGroupDn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la création du groupe {GroupName}", groupName);
            throw;
        }
    }

    /// <summary>
    ///     Ajoute un utilisateur à un groupe AD (attribut member)
    /// </summary>
    /// <param name="userDn">DistinguishedName de l'utilisateur</param>
    /// <param name="groupDn">DistinguishedName du groupe</param>
    public void AddUserToGroup(string userDn, string groupDn)
    {
        if (!EnsureConnectionAsync().GetAwaiter().GetResult())
        {
            _logger.LogError("❌ AddUserToGroup: LDAP indisponible, impossible d'ajouter {UserDn} au groupe {GroupDn}",
                userDn, groupDn);
            throw new InvalidOperationException(
                "Service LDAP indisponible. Impossible d'ajouter l'utilisateur au groupe.");
        }

        try
        {
            var req = new ModifyRequest(groupDn, DirectoryAttributeOperation.Add, "member", userDn);
            _connection.SendRequest(req);
            _logger.LogInformation("✅ Utilisateur {UserDn} ajouté au groupe {GroupDn}", userDn, groupDn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de l'ajout de l'utilisateur {UserDn} au groupe {GroupDn}", userDn,
                groupDn);
            throw;
        }
    }

    public void AddGroupToGroup(string childGroupDn, string parentGroupDn)
    {
        AddMemberToGroup(childGroupDn, parentGroupDn);
    }

    /// <summary>
    ///     Déplace un utilisateur d'une OU vers une autre sans AccountManagement
    ///     (pure requête LDAP : ModifyDNRequest)
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
    ///     Récupère l'OU actuelle d'un utilisateur
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

    /*====================================================================
     *  A INSÉRER DANS LA CLASSE LdapService
     *  (et à déclarer dans l'interface ILdapService)
     *==================================================================*/

    #region --- 1)  CONTENEURS (objectClass=container) -------------------

    public async Task<List<ContainerModel>> GetContainersAsync(string parentDn)
    {
        var list = new List<ContainerModel>();

        if (!await EnsureConnectionAsync() || string.IsNullOrWhiteSpace(parentDn))
            return list;

        var req = new SearchRequest(
            parentDn,
            "(objectClass=container)", // 1 : conteneurs "Users", "Computers", etc.
            SearchScope.OneLevel, "cn", "distinguishedName");

        var res = (SearchResponse)_connection.SendRequest(req);

        foreach (SearchResultEntry e in res.Entries)
            list.Add(new ContainerModel
            {
                DistinguishedName = e.DistinguishedName,
                Name = e.Attributes["cn"][0]?.ToString() ?? ExtractNameFromDn(e.DistinguishedName)
            });

        _logger.LogDebug("✅ {Count} containers trouvés sous {Parent}", list.Count, parentDn);
        return list;
    }

    #endregion

    #region --- 2)  OUs enfants directs ----------------------------------

    public async Task<List<OrganizationalUnitModel>> GetOrganizationalUnitsAsync(string parentDn)
    {
        var list = new List<OrganizationalUnitModel>();

        if (!await EnsureConnectionAsync() || string.IsNullOrWhiteSpace(parentDn))
            return list;

        var req = new SearchRequest(
            parentDn,
            "(objectClass=organizationalUnit)",
            SearchScope.OneLevel, "ou", "distinguishedName");

        var res = (SearchResponse)_connection.SendRequest(req);

        foreach (SearchResultEntry e in res.Entries)
            list.Add(new OrganizationalUnitModel
            {
                DistinguishedName = e.DistinguishedName,
                Name = GetOuNameFromEntry(e)
            });

        _logger.LogDebug("✅ {Count} OUs trouvées sous {Parent}", list.Count, parentDn);
        return list;
    }

    #endregion

    #region --- 3)  Utilisateurs enfants directs -------------------------

    public async Task<List<UserModel>> GetUsersAsync(string parentDn, int maxResults = 50)
    {
        var users = new List<UserModel>();

        if (!await EnsureConnectionAsync() || string.IsNullOrWhiteSpace(parentDn))
            return users;

        var req = new SearchRequest(
            parentDn,
            "(objectClass=user)",
            SearchScope.OneLevel, "sAMAccountName", "cn", "givenName", "sn", "displayName", "userPrincipalName", "mail",
            "userAccountControl", "description", "distinguishedName");

        var res = (SearchResponse)_connection.SendRequest(req);

        foreach (var e in res.Entries.Cast<SearchResultEntry>().Take(maxResults))
        {
            var attrs = e.Attributes;
            
            // Construire le dictionnaire d'attributs additionnels avec le DN correct
            var additionalAttributes = attrs.AttributeNames
                .Cast<string>()
                .ToDictionary(a => a, a => attrs[a][0]?.ToString() ?? "");
            
            // S'assurer que distinguishedName utilise la propriété native de l'entrée
            additionalAttributes["distinguishedName"] = e.DistinguishedName;
            
            users.Add(new UserModel
            {
                SamAccountName = attrs["sAMAccountName"]?[0]?.ToString() ?? "",
                GivenName = attrs["givenName"]?[0]?.ToString(),
                Surname = attrs["sn"]?[0]?.ToString(),
                DisplayName = attrs["displayName"]?[0]?.ToString(),
                UserPrincipalName = attrs["userPrincipalName"]?[0]?.ToString(),
                Email = attrs["mail"]?[0]?.ToString(),
                OrganizationalUnit = parentDn,
                AdditionalAttributes = additionalAttributes
            });
        }

        _logger.LogDebug("✅ {Count} users trouvés sous {Parent}", users.Count, parentDn);
        return users;
    }

    #endregion

    #region --- 5)  Action unitaire pour le bulk -------------------------

    public async Task DoBulkActionAsync(string userDn, ActiveDirectoryController.BulkActionRequestDto request)
    {
        if (!await EnsureConnectionAsync())
            throw new InvalidOperationException("LDAP indisponible.");

        try
        {
            switch (request.Action.ToLowerInvariant())
            {
                case "resetpassword":
                    if (string.IsNullOrWhiteSpace(request.NewPassword))
                        throw new ArgumentException("NewPassword requis");
                    SetPassword(userDn, request.NewPassword);
                    EnableAccount(userDn); // facultatif
                    break;

                case "disableaccounts":
                    DisableAccount(userDn);
                    break;

                case "enableaccounts":
                    EnableAccount(userDn);
                    break;

                case "unlockaccounts":
                    UnlockAccount(userDn);
                    break;

                case "adddescription":
                    if (string.IsNullOrWhiteSpace(request.Description))
                        throw new ArgumentException("Description requise");
                    var mod = new ModifyRequest(
                        userDn,
                        DirectoryAttributeOperation.Replace,
                        "description",
                        request.Description);
                    _connection.SendRequest(mod);
                    break;

                case "movetou":
                    if (string.IsNullOrWhiteSpace(request.TargetOU))
                        throw new ArgumentException("TargetOU requis");
                    await MoveUserAsync(
                        GetSamFromDn(userDn),
                        ExtractOuFromDn(userDn),
                        request.TargetOU);
                    break;

                default:
                    _logger.LogWarning("Action inconnue : {Action}", request.Action);
                    break;
            }

            _logger.LogInformation("✅ BulkAction '{Action}' exécutée pour {Dn}", request.Action, userDn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ BulkAction '{Action}' échouée pour {Dn}", request.Action, userDn);
            throw;
        }
    }

    #endregion


    public void Dispose()
    {
        _connection?.Dispose();
        _logger.LogDebug("LDAP connection disposed");
    }

    /// <summary>
    ///     Initialise la connexion LDAP de manière résiliente
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
            _logger.LogWarning(ex,
                "⚠️ Error initializing LDAP connection: {Message}. Service continuera en mode dégradé.", ex.Message);
            _ldapAvailable = false;
            _connectionInitialized = false;
            return false;
        }
    }

    /// <summary>
    ///     Assure que la connexion LDAP est établie (lazy loading + retry logic)
    /// </summary>
    private async Task<bool> EnsureConnectionAsync()
    {
        // Si connexion déjà établie et disponible
        if (_connectionInitialized && _ldapAvailable && _connection != null) return true;

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
            if (_connectionInitialized && _ldapAvailable && _connection != null) return true;

            _lastConnectionAttempt = DateTime.Now;

            // Tentative de connexion
            var task = InitializeLdapConnection();
            var result = task.GetAwaiter().GetResult();

            if (result)
                _logger.LogInformation("✅ Connexion LDAP rétablie avec succès");
            else
                _logger.LogWarning("❌ Connexion LDAP échouée, retry dans {Minutes} minutes",
                    _retryInterval.TotalMinutes);

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

    /*public void CreateUser(string firstName, string lastName, string password, string ouDn)
    {
        if (!EnsureConnectionAsync().GetAwaiter().GetResult())
        {
            _logger.LogError(
                "❌ CreateUser: LDAP indisponible, impossible de créer l'utilisateur {FirstName} {LastName}", firstName,
                lastName);
            throw new InvalidOperationException("Service LDAP indisponible. Impossible de créer l'utilisateur.");
        }

        try
        {
            var cn = Escape(firstName + " " + lastName);
            var dn = $"CN={cn},{ouDn}";
            var sam = GenerateSam(firstName, lastName);

            // Vérifier si _baseDn est null et utiliser un domaine par défaut si nécessaire
            var domainPart = "local";
            if (!string.IsNullOrEmpty(_baseDn))
                domainPart = _baseDn.Replace(",DC=", ".").Replace("DC=", "");
            else
                _logger.LogWarning("BaseDn est null. Utilisation d'un domaine par défaut pour UPN: 'local'");

            var upn = $"{firstName.ToLower()}.{lastName.ToLower()}@{domainPart}".ToLower();

            var attrs = new[]
            {
                new DirectoryAttribute("objectClass", "top", "person", "organizationalPerson", "user"),
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
            _logger.LogError(ex, "❌ Erreur lors de la création de l'utilisateur {FirstName} {LastName}", firstName,
                lastName);
            throw;
        }
    }*/

    public bool OrganizationalUnitExists(string ouDn)
    {
        try
        {
            if (!EnsureConnectionAsync().GetAwaiter().GetResult())
            {
                _logger.LogWarning(
                    "⚠️ OrganizationalUnitExists: LDAP indisponible, retour false par défaut pour {OuDn}", ouDn);
                return false;
            }

            var req = new SearchRequest(ouDn, "(objectClass=organizationalUnit)", SearchScope.Base);
            var res = (SearchResponse)_connection.SendRequest(req);
            var exists = res.Entries.Count > 0;

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
        var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, "cn", "distinguishedName");
        var res = (SearchResponse)_connection.SendRequest(req);
        if (res.Entries.Count == 0) return null;
        var e = res.Entries[0];
        return new LdapUser
        {
            FullName = e.Attributes["cn"][0].ToString(),
            DistinguishedName = e.DistinguishedName
        };
    }

    /// <summary>
    ///     ✅ Valide et extrait les attributs essentiels (givenName, sn, password)
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
            firstName = parts.Length >= 1
                ? char.ToUpper(parts[0][0]) + parts[0].Substring(1).ToLower()
                : samAccountName;
            _logger.LogWarning("⚠️ givenName manquant, dérivé depuis sAMAccountName: '{FirstName}'", firstName);
        }

        // Extraction du nom de famille
        attributes.TryGetValue("sn", out var lastName);
        if (string.IsNullOrWhiteSpace(lastName))
        {
            var parts = samAccountName.Split('.');
            lastName = parts.Length >= 2 ? parts[1].ToUpper() : "USER";
            _logger.LogWarning("⚠️ sn manquant, dérivé depuis sAMAccountName: '{LastName}'", lastName);
        }

        // Extraction du mot de passe
        var password = attributes.GetValueOrDefault("password") ??
                       attributes.GetValueOrDefault("userPassword") ??
                       "TempPass123!"; // ✅ Utilise le même défaut que la configuration

        if (password == "TempPass123!")
            _logger.LogInformation("🔑 Mot de passe par défaut du système utilisé pour {SamAccountName}", samAccountName);

        return (firstName, lastName, password);
    }



    /// <summary>
    ///     ✅ Prépare tous les attributs Active Directory depuis la configuration uniquement
    /// </summary>
    private DirectoryAttribute[] PrepareAllAdAttributes(string samAccountName,
        Dictionary<string, string> attributes, string firstName, string lastName)
    {
        var attrs = new List<DirectoryAttribute>();

        // ✅ Attributs système obligatoires (toujours nécessaires pour la création)
        attrs.Add(new DirectoryAttribute("objectClass", "top", "person", "organizationalPerson", "user"));
        
        // ✅ CORRECTION : Créer un compte DÉSACTIVÉ initialement (requis pour LdapConnection)
        // Le compte sera activé après définition du mot de passe
        const int UF_NORMAL_ACCOUNT = 0x0200;
        const int UF_ACCOUNTDISABLE = 0x0002;
        const int UF_PASSWD_NOTREQD = 0x0020;
        var userAccountControl = UF_NORMAL_ACCOUNT | UF_ACCOUNTDISABLE | UF_PASSWD_NOTREQD;
        attrs.Add(new DirectoryAttribute("userAccountControl", userAccountControl.ToString()));

        // ✅ Attributs système à exclure (gérés séparément)
        var systemAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "objectClass", "userAccountControl", "password", "userPassword", "unicodePwd"
        };

        // ✅ Règles de validation par attribut (longueurs max selon les limitations AD)
        var attributeValidationRules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["initials"] = 6,
            ["homeDrive"] = 3,
            ["postalCode"] = 10,
            ["telephoneNumber"] = 64,
            ["mobile"] = 64,
            ["pager"] = 64,
            ["facsimileTelephoneNumber"] = 64
        };

        // ✅ Parcourir TOUS les attributs de la configuration
        foreach (var attribute in attributes)
        {
            var attributeName = attribute.Key;
            var attributeValue = attribute.Value;

            // Ignorer les attributs système ou vides
            if (systemAttributes.Contains(attributeName) || string.IsNullOrWhiteSpace(attributeValue))
            {
                continue;
            }

            try
            {
                // Appliquer les règles de validation si elles existent
                var validatedValue = attributeValue.Trim();
                if (attributeValidationRules.TryGetValue(attributeName, out var maxLength))
                {
                    validatedValue = ValidateAndTruncateAttribute(attributeName, validatedValue, maxLength);
                }

                // Ajouter l'attribut à la liste
                attrs.Add(new DirectoryAttribute(attributeName, validatedValue));
                _logger.LogDebug("✅ Attribut {AttributeName} ajouté: '{Value}'", attributeName, validatedValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Impossible d'ajouter l'attribut {AttributeName}: {Error}", attributeName, ex.Message);
            }
        }

        _logger.LogInformation("📝 {Count} attributs AD préparés pour {SamAccountName}", attrs.Count, samAccountName);
        return attrs.ToArray();
    }

    /// <summary>
    ///     ✅ Valide et tronque un attribut à la longueur maximale autorisée par AD
    /// </summary>
    private string ValidateAndTruncateAttribute(string attributeName, string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (value.Length <= maxLength)
            return value;

        var truncated = value.Substring(0, maxLength);
        _logger.LogWarning(
            "⚠️ Attribut {AttributeName} tronqué de {OriginalLength} à {MaxLength} caractères: '{Original}' -> '{Truncated}'",
            attributeName, value.Length, maxLength, value, truncated);

        return truncated;
    }

    /// <summary>
    ///     ✅ Génère un UserPrincipalName approprié
    /// </summary>
    private string GenerateUserPrincipalName(string samAccountName, Dictionary<string, string> attributes)
    {
        // Utiliser la valeur mappée si disponible
        if (attributes.TryGetValue("userPrincipalName", out var mappedUpn) && !string.IsNullOrWhiteSpace(mappedUpn))
            return mappedUpn;

        // Construire depuis le domaine
        var domainPart = "local";
        if (!string.IsNullOrEmpty(_baseDn)) domainPart = _baseDn.Replace(",DC=", ".").Replace("DC=", "");

        return $"{samAccountName}@{domainPart}";
    }

    /// <summary>
    ///     ✅ Construit le UserModel de retour avec tous les attributs
    /// </summary>
    private UserModel BuildUserModel(string samAccountName, Dictionary<string, string> attributes,
        string distinguishedName, string firstName, string lastName, string ouDn)
    {
        var domainPart = "local";
        if (!string.IsNullOrEmpty(_baseDn)) domainPart = _baseDn.Replace("DC=", "").Replace(",", ".");

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


    /// <summary>
    ///     ✅ NOUVELLE MÉTHODE : Extrait le nom d'une OU depuis une entrée LDAP avec protection
    /// </summary>
    private string GetOuNameFromEntry(SearchResultEntry entry)
    {
        try
        {
            // Essayer d'abord l'attribut "ou"
            if (entry.Attributes.Contains("ou") && entry.Attributes["ou"].Count > 0)
            {
                var ouValue = entry.Attributes["ou"][0]?.ToString();
                if (!string.IsNullOrWhiteSpace(ouValue)) return ouValue;
            }

            // Sinon, extraire depuis le DN
            return ExtractNameFromDn(entry.DistinguishedName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Erreur lors de l'extraction du nom OU depuis l'entrée: {DN}",
                entry.DistinguishedName);
            return ExtractNameFromDn(entry.DistinguishedName);
        }
    }

    /// <summary>
    ///     ✅ MÉTHODE UTILITAIRE AMÉLIORÉE : Extrait le nom depuis un Distinguished Name
    /// </summary>
    private string ExtractNameFromDn(string distinguishedName)
    {
        if (string.IsNullOrEmpty(distinguishedName))
            return "Inconnu";

        try
        {
            var parts = distinguishedName.Split(',');
            if (parts.Length > 0)
            {
                var firstPart = parts[0].Trim();
                if (firstPart.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                    return firstPart.Substring(3);
                if (firstPart.StartsWith("OU=", StringComparison.OrdinalIgnoreCase))
                    return firstPart.Substring(3);
                if (firstPart.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                    return firstPart.Substring(3);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Erreur lors de l'extraction du nom depuis le DN: {DN}", distinguishedName);
        }

        return distinguishedName;
    }

    private string GetUserDn(string sam)
    {
        if (string.IsNullOrEmpty(_baseDn))
        {
            _logger.LogWarning(
                "BaseDn est null lors de la recherche du DN de l'utilisateur {Sam}. Impossible de retrouver le DN exact.",
                sam);
            throw new InvalidOperationException($"BaseDn non configuré. Impossible de retrouver le DN pour {sam}");
        }

        var filter = $"(sAMAccountName={Escape(sam)})";
        var req = new SearchRequest(_baseDn, filter, SearchScope.Subtree, "distinguishedName");
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
    
    /// <summary>
    /// ✅ MÉTHODE FALLBACK : Crée un utilisateur directement avec DirectoryEntry (plus compatible et propre)
    /// </summary>
    private async Task CreateUserWithDirectoryEntry(string samAccountName, Dictionary<string, string> attributes, string ouDn, string firstName, string lastName, string password)
    {
        try
        {
            _logger.LogInformation("🔄 Création utilisateur avec DirectoryEntry pour {SamAccountName}", samAccountName);
            
            // ✅ Récupérer les informations de connexion LDAP
            var server = await _ldapSettingsProvider.GetServerAsync();
            var username = await _ldapSettingsProvider.GetUsernameAsync();
            var ldapPassword = await _ldapSettingsProvider.GetPasswordAsync();
            
            // ✅ Créer l'entrée DirectoryEntry pour l'OU cible avec authentification
            var ldapPath = $"LDAP://{server}/{ouDn}";
            using var ouEntry = new DirectoryEntry(ldapPath, username, ldapPassword, AuthenticationTypes.Secure);
            
            // ✅ Créer l'utilisateur directement dans la bonne OU
            using var userEntry = ouEntry.Children.Add($"CN={firstName} {lastName}", "user");
            
            // ✅ ÉTAPE 1: Attributs essentiels obligatoires SEULEMENT (création minimale)
            userEntry.Properties["sAMAccountName"].Value = samAccountName;
            userEntry.Properties["userPrincipalName"].Value = GenerateUserPrincipalName(samAccountName, attributes);
            userEntry.Properties["givenName"].Value = firstName;
            userEntry.Properties["sn"].Value = lastName;
            userEntry.Properties["displayName"].Value = $"{firstName} {lastName}";
            
            // ✅ ÉTAPE 1: Compte DÉSACTIVÉ initialement (obligatoire pour la création)
            const int UF_NORMAL_ACCOUNT = 0x00000200;
            const int UF_ACCOUNTDISABLE = 0x00000002;
            const int UF_PASSWD_NOTREQD = 0x00000020;
            userEntry.Properties["userAccountControl"].Value = UF_NORMAL_ACCOUNT | UF_ACCOUNTDISABLE | UF_PASSWD_NOTREQD;
            
            // ✅ ÉTAPE 1: Sauvegarder la création de base (compte désactivé)
            userEntry.CommitChanges();
            _logger.LogInformation("✅ Utilisateur de base créé (désactivé) avec DirectoryEntry: {SamAccountName}", samAccountName);
            
            // ✅ ÉTAPE 2: Définir le mot de passe
            userEntry.Invoke("SetPassword", password);
            userEntry.CommitChanges();
            _logger.LogInformation("✅ Mot de passe défini pour {SamAccountName}", samAccountName);
            
            // ✅ ÉTAPE 3: Activer le compte maintenant que le mot de passe est défini
            const int UF_DONT_EXPIRE_PASSWD = 0x00010000;
            const int UF_PASSWORD_EXPIRED = 0x00800000;
            userEntry.Properties["userAccountControl"].Value = UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWD | UF_PASSWORD_EXPIRED;
            userEntry.CommitChanges();
            _logger.LogInformation("✅ Compte activé avec changement de mot de passe forcé pour {SamAccountName}", samAccountName);
            
            // ✅ ÉTAPE 4: Ajouter les autres attributs un par un (plus sûr)
            bool hasAttributesToAdd = false;
            foreach (var attr in attributes.Where(kv => !IsEssentialAttribute(kv.Key)))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(attr.Value))
                    {
                        userEntry.Properties[attr.Key].Value = attr.Value;
                        hasAttributesToAdd = true;
                    }
                }
                catch (Exception attrEx)
                {
                    _logger.LogWarning(attrEx, "⚠️ Impossible d'ajouter l'attribut {AttributeName} pour {SamAccountName}", attr.Key, samAccountName);
                }
            }
            
            // ✅ ÉTAPE 4: Sauvegarder les attributs supplémentaires seulement s'il y en a
            if (hasAttributesToAdd)
            {
                userEntry.CommitChanges();
                _logger.LogInformation("✅ Attributs supplémentaires ajoutés pour {SamAccountName}", samAccountName);
            }
            else
            {
                _logger.LogInformation("✅ Aucun attribut supplémentaire à ajouter pour {SamAccountName}", samAccountName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erreur lors de la création avec DirectoryEntry pour {SamAccountName}", samAccountName);
            throw;
        }
    }
    
    /// <summary>
    /// Détermine si un attribut est essentiel (déjà défini lors de la création de base)
    /// </summary>
    private bool IsEssentialAttribute(string attributeName)
    {
        var essentialAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sAMAccountName", "userPrincipalName", "givenName", "sn", "surname",
            "displayName", "name", "cn", "userAccountControl", "objectClass",
            "password", "userPassword", "unicodePwd" // ✅ Mots de passe gérés séparément
        };
        
        return essentialAttributes.Contains(attributeName);
    }



    private void EnableAccount(string userDn)
    {
        const int UF_NORMAL_ACCOUNT = 0x0200;
        const int UF_DONT_EXPIRE_PASSWD = 0x10000;
        const int UF_PASSWORD_EXPIRED = 0x00800000;
        
        // ✅ CORRECTION : Compte activé avec changement de mot de passe forcé à la première connexion
        var val = (UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWD | UF_PASSWORD_EXPIRED).ToString();
        _connection.SendRequest(new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, "userAccountControl",
            val));
        _logger.LogInformation("Account enabled with password change required {Dn}", userDn);
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

    /// <summary>
    /// ✅ Génère un sAMAccountName conforme aux contraintes Active Directory
    /// </summary>
    private string GenerateSam(string firstName, string lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(lastName))
            return "user" + DateTime.Now.ToString("mmss");

        var fn = string.IsNullOrWhiteSpace(firstName) ? "user" : firstName.ToLowerInvariant().Trim();
        var ln = string.IsNullOrWhiteSpace(lastName) ? "user" : lastName.ToLowerInvariant().Trim();

        // ✅ Supprimer les caractères spéciaux et accentués
        fn = RemoveDiacriticsAndSpecialChars(fn);
        ln = RemoveDiacriticsAndSpecialChars(ln);

        var samAccountName = $"{fn}.{ln}";

        // ✅ CONTRAINTE AD : Ne pas commencer par un chiffre
        if (!string.IsNullOrEmpty(samAccountName) && char.IsDigit(samAccountName[0]))
        {
            samAccountName = "u" + samAccountName;
        }

        // ✅ CONTRAINTE AD : Limiter à 20 caractères en gardant le format prénom.nom
        if (samAccountName.Length > 20)
        {
            // Réduire progressivement pour tenir dans 20 caractères
            while ($"{fn}.{ln}".Length > 20 && fn.Length > 1)
            {
                fn = fn.Substring(0, fn.Length - 1);
            }
            
            while ($"{fn}.{ln}".Length > 20 && ln.Length > 1)
            {
                ln = ln.Substring(0, ln.Length - 1);
            }
            
            samAccountName = $"{fn}.{ln}";
            
            // Si toujours trop long, tronquer brutalement
            if (samAccountName.Length > 20)
            {
                samAccountName = samAccountName.Substring(0, 20);
            }
        }

        // ✅ Nettoyer le résultat final
        samAccountName = samAccountName.TrimEnd('.');
        
        return string.IsNullOrEmpty(samAccountName) ? "user" + DateTime.Now.ToString("mmss") : samAccountName;
    }

    /// <summary>
    /// ✅ Supprime les accents et caractères spéciaux pour les noms de compte AD
    /// </summary>
    private string RemoveDiacriticsAndSpecialChars(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        // Supprimer les accents
        var normalizedString = input.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder();

        foreach (var c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                stringBuilder.Append(c);
        }

        var result = stringBuilder.ToString().Normalize(NormalizationForm.FormC);

        // ✅ Remplacer les caractères de séparation par des points (comme dans NormalizeSamAccountName)
        result = result
            .Replace(" ", ".")
            .Replace("'", ".")
            .Replace("-", ".") // ✅ FIX : Remplacer les tirets par des points au lieu de les supprimer
            .Replace("_", ".");

        // ✅ Supprimer les autres caractères interdits dans sAMAccountName
        var forbiddenChars = new char[] 
        { 
            '"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', 
            '@', '#', '$', '%', '^', '&', '(', ')', '{', '}', '!', '~', '`'
        };
        
        foreach (var forbiddenChar in forbiddenChars)
        {
            result = result.Replace(forbiddenChar.ToString(), "");
        }

        // ✅ Remplacer les séquences multiples de points par un seul point (comme dans NormalizeSamAccountName)
        result = System.Text.RegularExpressions.Regex.Replace(result, "\\.+", ".");
        
        // ✅ Supprimer les points au début et à la fin
        result = result.Trim('.');

        return result;
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

    /// <summary>
    ///     ✅ Méthode utilitaire pour extraire l'OU depuis un DN
    /// </summary>
    private string ExtractOuFromDn(string distinguishedName)
    {
        if (string.IsNullOrEmpty(distinguishedName)) return string.Empty;

        // Retirer la partie CN=username, pour ne garder que l'OU
        var parts = distinguishedName.Split(',');
        if (parts.Length > 1) return string.Join(",", parts.Skip(1));

        return distinguishedName;
    }

    /// <summary>
    ///     ✅ MÉTHODE UTILITAIRE : Compare intelligemment deux valeurs d'attributs
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
            return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);

        if (caseSensitiveAttributes.Contains(attributeName))
            return string.Equals(value1, value2, StringComparison.Ordinal);

        // Par défaut, comparaison insensible à la casse
        return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     ✅ MÉTHODE UTILITAIRE : Met à jour les attributs spécifiques d'un utilisateur
    /// </summary>
    private async Task UpdateUserAttributesAsync(string samAccountName, Dictionary<string, string> attributesToUpdate)
    {
        try
        {
            var userDn = GetUserDn(samAccountName);

            foreach (var attr in attributesToUpdate)
            {
                var modifyRequest =
                    new ModifyRequest(userDn, DirectoryAttributeOperation.Replace, attr.Key, attr.Value);
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
    ///     Ajoute un groupe comme membre d'un autre groupe AD (attribut member)
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

    /// <summary>
    ///     Extrait le chemin de l'OU depuis un DN complet
    ///     Exemple: "CN=John Doe,OU=Users,OU=IT,DC=domain,DC=com" -> "OU=Users,OU=IT,DC=domain,DC=com"
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

        var startIndex = -1;
        if (ouIndex >= 0 && dcIndex >= 0)
            startIndex = Math.Min(ouIndex, dcIndex);
        else if (ouIndex >= 0)
            startIndex = ouIndex;
        else if (dcIndex >= 0) startIndex = dcIndex;

        if (startIndex >= 0) return distinguishedName.Substring(startIndex + 1); // +1 pour ignorer la virgule

        return string.Empty;
    }

    /*====================================================================
     *  PETITES MÉTHODES PRIVÉES D'APPUI
     *==================================================================*/
    private void DisableAccount(string dn)
    {
        // 514 = NORMAL_ACCOUNT + DISABLED
        _connection.SendRequest(new ModifyRequest(
            dn,
            DirectoryAttributeOperation.Replace,
            "userAccountControl",
            "514"));
        _logger.LogInformation("Compte désactivé {Dn}", dn);
    }

    private void UnlockAccount(string dn)
    {
        _connection.SendRequest(new ModifyRequest(
            dn,
            DirectoryAttributeOperation.Replace,
            "lockoutTime",
            "0"));
        _logger.LogInformation("Compte déverrouillé {Dn}", dn);
    }

    private static string GetSamFromDn(string dn)
    {
        var cn = dn.Split(',')[0]; // "CN=John Doe"
        return cn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)
            ? cn[3..].Replace(" ", ".").ToLowerInvariant()
            : cn.ToLowerInvariant();
    }

    /*====================================================================
     *  MODELE "container" (optionnel : placez-le où vous rangez vos DTO)
     *==================================================================*/
    public class ContainerModel
    {
        public string DistinguishedName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    #region --- 4)  Recherche générique d'utilisateurs -------------------

    /*public async Task<List<UserModel>> SearchUsersAsync(string baseDn, string ldapFilter)
    {
        var list = new List<UserModel>();

        if (!await EnsureConnectionAsync() || string.IsNullOrWhiteSpace(baseDn))
            return list;

        var req = new SearchRequest(
            baseDn,
            ldapFilter,                     // ex. "(&(objectClass=user)(|(cn=*toto*)(mail=*toto*)))"
            SearchScope.Subtree,
            new[] { "sAMAccountName", "cn", "givenName", "sn", "displayName",
                    "userPrincipalName", "mail", "userAccountControl", "description",
                    "distinguishedName" });

        var res = (SearchResponse)_connection.SendRequest(req);

        foreach (SearchResultEntry e in res.Entries)
        {
            var attrs = e.Attributes;
            list.Add(new UserModel
            {
                SamAccountName    = attrs["sAMAccountName"]?[0]?.ToString() ?? "",
                GivenName         = attrs["givenName"]?[0]?.ToString(),
                Surname           = attrs["sn"]?[0]?.ToString(),
                DisplayName       = attrs["displayName"]?[0]?.ToString(),
                UserPrincipalName = attrs["userPrincipalName"]?[0]?.ToString(),
                Email             = attrs["mail"]?[0]?.ToString(),
                OrganizationalUnit= ExtractOuFromDn(e.DistinguishedName),
                AdditionalAttributes = attrs.AttributeNames
                                            .Cast<string>()
                                            .ToDictionary(a => a,
                                                          a => attrs[a][0]?.ToString() ?? ""),
            });
        }

        _logger.LogDebug("✅ {Count} users trouvés par recherche", list.Count);
        return list;
    }*/

    #endregion

    /// <summary>
    /// ✅ NOUVELLE MÉTHODE BATCH : Récupère uniquement les sAMAccountNames d'une OU pour la détection d'orphelins
    /// Plus rapide que GetAllUsersInOuAsync car ne récupère que le strict nécessaire
    /// </summary>
    public async Task<List<string>> GetAllSamAccountNamesInOuBatchAsync(string ouPath)
    {
        var samAccountNames = new List<string>();

        try
        {
            if (!await EnsureConnectionAsync())
            {
                _logger.LogWarning("⚠️ GetAllSamAccountNamesInOuBatchAsync: LDAP indisponible, retour liste vide pour {OuPath}", ouPath);
                return samAccountNames;
            }

            // ✅ Requête LDAP ultra-optimisée : récupérer seulement sAMAccountName
            var filter = "(objectClass=user)";
            var req = new SearchRequest(ouPath, filter, SearchScope.Subtree, "sAMAccountName");

            var res = (SearchResponse)_connection.SendRequest(req);

            foreach (SearchResultEntry entry in res.Entries)
            {
                var samAccountName = GetAttributeValue(entry, "sAMAccountName");
                if (!string.IsNullOrEmpty(samAccountName))
                {
                    // ✅ Nettoyer directement ici comme dans l'analyse
                    var cleanedSam = samAccountName.Split('(')[0].Trim();
                    if (!string.IsNullOrEmpty(cleanedSam))
                        samAccountNames.Add(cleanedSam);
                }
            }

            _logger.LogInformation("⚡ Récupéré {Count} sAMAccountNames depuis l'OU {OuPath} (batch optimisé)", samAccountNames.Count, ouPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération batch des sAMAccountNames dans l'OU {OuPath}", ouPath);
        }

        return samAccountNames;
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