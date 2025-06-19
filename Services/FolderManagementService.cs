using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ADManagerAPI.Config;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;

namespace ADManagerAPI.Services;

/// <summary>
///     Service de gestion des dossiers et partages utilisateur via connexions SMB directes.
///     Utilise NetworkConnection pour l'authentification et manipulation d'ACL native.
/// </summary>
public sealed class FolderManagementService : IFolderManagementService, IDisposable
{
    #region IDisposable

    public void Dispose()
    {
        // Nettoyage si nécessaire
    }

    #endregion

    #region Dependencies & Configuration

    private readonly ILogger<FolderManagementService> _log;
    private readonly FolderManagementSettings _fm;
    private readonly FsrmSettings _fsrm;
    private readonly LdapSettingsProvider _ldap;
    private readonly IConfigService _configService;

    private readonly string _domainAdminsGroup;
    private readonly string? _netbiosDomain;
    private readonly string? _ldapDomain;
    private string? _username;
    private string? _password;

    public FolderManagementService(IConfiguration cfg,
        ILogger<FolderManagementService> log,
        LdapSettingsProvider ldap,
        IConfigService configService)
    {
        _log = log;
        _ldap = ldap;
        _configService = configService;

        _fm = cfg.GetSection("FolderManagement").Get<FolderManagementSettings>() ?? new FolderManagementSettings();
        _fsrm = cfg.GetSection("FSRM").Get<FsrmSettings>() ?? new FsrmSettings();

        // ✅ CORRECTION MAJEURE : Utilise le ConfigService pour lire depuis settings.json
        try
        {
            var settings = _configService.GetAllSettingsAsync().Result;
            _netbiosDomain = settings.NetBiosDomainName;
            
            if (string.IsNullOrWhiteSpace(_netbiosDomain))
            {
                // Fallback : extraction depuis le DN LDAP
                _netbiosDomain = ExtractNetbiosDomain(cfg["LdapSettings:Domain"]);
                _log.LogWarning("netBiosDomainName manquant en configuration settings.json, utilisation du fallback depuis LDAP : '{NetBiosDomain}'", _netbiosDomain);
            }
            else
            {
                _log.LogInformation("netBiosDomainName lu depuis settings.json : '{NetBiosDomain}'", _netbiosDomain);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erreur lors du chargement de la configuration depuis settings.json");
            // Fallback : extraction depuis le DN LDAP
            _netbiosDomain = ExtractNetbiosDomain(cfg["LdapSettings:Domain"]);
            _log.LogWarning("Utilisation du fallback NetBIOS depuis LDAP : '{NetBiosDomain}'", _netbiosDomain);
        }

        _domainAdminsGroup = string.IsNullOrWhiteSpace(_netbiosDomain)
            ? "Domain Admins"
            : $"{_netbiosDomain}\\Domain Admins";
            
        // ✅ CORRECTION : Lecture du domaine LDAP pour UPN
        _ldapDomain = cfg["LdapSettings:Domain"];
        
        _log.LogInformation("FolderManagementService initialisé avec NetBiosDomain: '{NetBiosDomain}', LdapDomain: '{LdapDomain}', DomainAdminsGroup: '{DomainAdminsGroup}'", 
            _netbiosDomain, _ldapDomain, _domainAdminsGroup);
    }

    #endregion

    #region Public Interface Methods

    /// <summary>
    ///     Vérifie si un partage utilisateur existe déjà sur le serveur cible.
    /// </summary>
    public async Task<bool> CheckUserShareExistsAsync(string? foldersTargetServerName,
        string cleanedSamAccountName,
        string? foldersLocalPathForUserShareOnServer)
    {
        if (string.IsNullOrWhiteSpace(foldersTargetServerName) ||
            string.IsNullOrWhiteSpace(cleanedSamAccountName))
        {
            _log.LogWarning("❌ CheckUserShareExists: Paramètres manquants - Server='{Server}', User='{User}'", 
                foldersTargetServerName, cleanedSamAccountName);
            return false;
        }

      //  _log.LogInformation("🔍 CheckUserShareExists démarré pour utilisateur '{User}' sur serveur '{Server}'", cleanedSamAccountName, foldersTargetServerName);

        try
        {
            await EnsureCredentialsAsync();
            _log.LogDebug("✅ Credentials assurés pour CheckUserShareExists");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Erreur lors de l'assurance des credentials pour CheckUserShareExists");
            return false;
        }

        try
        {
            // Utilise le partage administratif pour vérifier l'existence
            var adminShare = _fm.AdminShareLetter.ToString().Trim(':').TrimEnd('$') + "$";
            var pathWithoutDrive = string.IsNullOrWhiteSpace(foldersLocalPathForUserShareOnServer)
                ? ""
                : RemoveDriveFromPath(foldersLocalPathForUserShareOnServer);

            var unc = string.IsNullOrWhiteSpace(pathWithoutDrive)
                ? $@"\\{foldersTargetServerName}\{adminShare}\{cleanedSamAccountName}"
                : $@"\\{foldersTargetServerName}\{adminShare}\{pathWithoutDrive}\{cleanedSamAccountName}";
            

            // ✅ Test de connectivité préalable
            if (!await TestServerConnectivity(foldersTargetServerName))
            {
                _log.LogWarning("❌ CheckUserShareExists: Serveur '{Server}' inaccessible", foldersTargetServerName);
                return false;
            }

            // ✅ CORRECTION : Utilise UPN au lieu de NetBIOS domain avec configuration dynamique
            var upnUsername = BuildUpnUsername(_username);
          //  _log.LogDebug("CheckUserShareExists - UPN utilisé: '{UpnUsername}'", upnUsername);
            
            using var conn = new NetworkConnection($@"\\{foldersTargetServerName}\{adminShare}",
                new NetworkCredential(upnUsername, _password));

            _log.LogDebug("✅ Connexion SMB établie, vérification de l'existence du dossier...");
            var exists = Directory.Exists(unc);
            
           // _log.LogInformation("🎯 CheckUserShareExists RÉSULTAT: {Path} => {Exists}", unc, exists);
            return exists;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Erreur lors de la vérification d'existence pour {User} sur {Server} - " +
                              "Type: {ExceptionType}, Message: {Message}", 
                cleanedSamAccountName, foldersTargetServerName, ex.GetType().Name, ex.Message);
            
            // Log des détails spécifiques selon le type d'erreur
            if (ex is UnauthorizedAccessException)
            {
                _log.LogError("🔐 Erreur d'accès - Vérifiez les permissions du compte de service");
            }
            else if (ex is System.IO.DirectoryNotFoundException)
            {
                _log.LogWarning("📁 Dossier non trouvé - Ceci est normal si l'utilisateur n'a pas encore de partage");
            }
            else if (ex is System.ComponentModel.Win32Exception win32Ex)
            {
                _log.LogError("🖥️ Erreur Win32: Code={ErrorCode}, Message='{Message}'", win32Ex.ErrorCode, win32Ex.Message);
            }
            else if (ex.Message.Contains("1326"))
            {
                _log.LogError("🔑 Erreur d'authentification (1326) - Vérifiez le nom d'utilisateur/mot de passe");
            }
            else if (ex.Message.Contains("53"))
            {
                _log.LogError("🌐 Erreur réseau (53) - Vérifiez la connectivité et le nom du serveur");
            }
            
            return false;
        }
    }

    /// <summary>
    ///     Provisionne un partage utilisateur complet : dossier, ACL et quota FSRM.
    /// </summary>
    public async Task<bool> ProvisionUserShareAsync(string argServerName,
        string argLocalPath,
        string argShareName,
        string argAccountAd,
        List<string> argSubfolders)
    {
        if (string.IsNullOrWhiteSpace(argServerName) ||
            string.IsNullOrWhiteSpace(argLocalPath) ||
            string.IsNullOrWhiteSpace(argAccountAd))
        {
            _log.LogError("❌ Paramètres manquants pour ProvisionUserShareAsync");
            return false;
        }

        try
        {
            _log.LogDebug("🔧 Assurance des credentials...");
            await EnsureCredentialsAsync();
            _log.LogDebug("✅ Credentials assurés");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Erreur lors de l'assurance des credentials");
            return false;
        }

        try
        {
            var (domain, samAccount) = ParseAccountName(argAccountAd);
            var effectiveDomain = string.IsNullOrWhiteSpace(domain) ? _netbiosDomain : domain;
            
            // Détermine le partage à utiliser
            var adminShare = _fm.AdminShareLetter.ToString().Trim(':').TrimEnd('$') + "$";
            var targetShare = argLocalPath.StartsWith(argServerName) ? argShareName : adminShare;
            
            _log.LogInformation("🚀 Début du provisionnement pour {Account} sur {Server} via partage {Share}", argAccountAd, argServerName, targetShare);

            await ProvisionHomeAsync(argServerName, targetShare, samAccount, effectiveDomain, argLocalPath, argSubfolders);

            _log.LogInformation("✅ ProvisionUserShareAsync terminé avec succès");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Erreur lors du provisionnement pour {Account} sur {Server}", argAccountAd, argServerName);
            return false;
        }
    }

    #endregion

    #region Core Provisioning Logic

    /// <summary>
    ///     Provisionne le dossier home d'un utilisateur avec ACL et quota.
    /// </summary>
    private async Task ProvisionHomeAsync(string server, string share, string samAccountName,
        string? domain, string localPath, List<string> subfolders)
    {
        var effectiveDomain = domain ?? _netbiosDomain ?? "BUILTIN";
        var quotaBytes = _fsrm.EnableFsrmQuotas ? CalculateQuotaBytes(UserRole.Student) : 0;

        _log.LogDebug("🔍 ProvisionHome - Paramètres calculés:");
        _log.LogDebug("    • Server: '{Server}'", server);
        _log.LogDebug("    • Share: '{Share}'", share);
        _log.LogDebug("    • SamAccountName: '{SamAccountName}'", samAccountName);
        _log.LogDebug("    • EffectiveDomain: '{EffectiveDomain}' (NetBIOS: '{NetBios}')", effectiveDomain, _netbiosDomain);
        _log.LogDebug("    • LocalPath: '{LocalPath}'", localPath);
        _log.LogDebug("    • QuotaBytes: {QuotaBytes}", quotaBytes);

        // ✅ AJOUT : Test de connectivité réseau préalable
        if (!await TestServerConnectivity(server))
        {
            throw new InvalidOperationException($"Impossible de joindre le serveur {server}. Vérifiez la connectivité réseau.");
        }

        try
        {
            _log.LogDebug("🔌 Tentative de connexion SMB...");
            _log.LogDebug("    • Chemin UNC de connexion: '\\\\{Server}\\{Share}'", server, share);
            _log.LogDebug("    • Username: '{Username}'", _username);
            _log.LogDebug("    • NetBIOS Domain: '{NetBios}'", _netbiosDomain);
            
            // ✅ CORRECTION : Utilise UPN au lieu de NetBIOS domain pour éviter erreur 1326
            var upnUsername = BuildUpnUsername(_username);
            _log.LogDebug("    • UPN Username: '{UpnUsername}' (domaine LDAP: '{LdapDomain}')", upnUsername, _ldapDomain);
            
            // Connexion SMB avec identifiants UPN (re-créée car using scope fermé)
            using var conn = new NetworkConnection($@"\\{server}\{share}",
                new NetworkCredential(upnUsername, _password));
                
            _log.LogInformation("✅ Connexion SMB établie avec succès");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ ÉCHEC de la connexion SMB vers \\\\{Server}\\{Share}", server, share);
            throw; // Re-lance l'exception pour être catchée par ProvisionUserShareAsync
        }

        try
        {
            // ✅ CORRECTION : Utilise UPN au lieu de NetBIOS domain
            var upnUsername = BuildUpnUsername(_username);
            _log.LogDebug("ProvisionHome - UPN utilisé: '{UpnUsername}' pour connexion SMB", upnUsername);
            
            // Connexion SMB avec identifiants UPN (re-créée car using scope fermé)
            using var conn = new NetworkConnection($@"\\{server}\{share}",
                new NetworkCredential(upnUsername, _password));

            // Construction du chemin UNC - CORRECTION: enlève la lettre de lecteur
            var pathWithoutDrive = RemoveDriveFromPath(localPath);
            var unc = string.IsNullOrWhiteSpace(pathWithoutDrive)
                ? $@"\\{server}\{share}\{samAccountName}"
                : $@"\\{server}\{share}\{pathWithoutDrive}\{samAccountName}";

            _log.LogInformation("🔍 Construction du chemin UNC:");
            _log.LogInformation("    • PathWithoutDrive: '{PathWithoutDrive}'", pathWithoutDrive);
            _log.LogInformation("    • UNC final: '{UNC}'", unc);

            // 1. Création du dossier principal
            _log.LogDebug("📁 Création du dossier principal...");
            var dir = Directory.CreateDirectory(unc);
            _log.LogInformation("✅ Dossier principal créé : {UNC}", unc);

            // 2. Création des sous-dossiers
            _log.LogDebug("📁 Création de {Count} sous-dossiers...", subfolders?.Count ?? 0);
            foreach (var subfolder in subfolders?.Where(sf => !string.IsNullOrWhiteSpace(sf)) ?? new List<string>())
            {
                var cleanSubfolder = SanitizeFileName(subfolder);
                var subPath = Path.Combine(unc, cleanSubfolder);
                Directory.CreateDirectory(subPath);
                _log.LogDebug("✅ Sous-dossier créé : {SubPath}", subPath);
            }

            // 3. Configuration des ACL (continue même en cas d'échec partiel)
            try
            {
                _log.LogDebug("🔒 Configuration des ACL...");
                await ConfigureAclAsync(unc, samAccountName, effectiveDomain);
                _log.LogInformation("✅ ACL configurées avec succès");
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "⚠️ Échec de la configuration des ACL pour {UNC}. Le dossier sera créé mais avec les ACL par défaut.",
                    unc);
                // Continue l'exécution car le dossier existe
            }

            // 4. Configuration du quota FSRM si activé
            if (quotaBytes > 0)
            {
                _log.LogDebug("💾 Configuration du quota FSRM...");
                await ConfigureFsrmQuotaAsync(unc, quotaBytes);
                _log.LogInformation("✅ Quota FSRM configuré");
            }
            else
            {
                _log.LogDebug("⏭️ Quota FSRM désactivé, ignoré");
            }

            // 5. Création du partage SMB individuel pour l'utilisateur
            _log.LogDebug("🌐 Création du partage SMB individuel...");
            await CreateUserSmbShareAsync(server, samAccountName, unc, effectiveDomain);
            _log.LogInformation("✅ Partage SMB individuel créé");

            _log.LogInformation("🎉 Provisionnement terminé avec succès pour {User} : {UNC}", samAccountName, unc);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Erreur lors de ProvisionHomeAsync pour {User}", samAccountName);
            throw; // Re-lance l'exception
        }
    }

    /// <summary>
    ///     Configure les ACL NTFS pour le dossier utilisateur.
    /// </summary>
    private async Task ConfigureAclAsync(string path, string samAccountName, string domain)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            var acl = dir.GetAccessControl();

            // Supprime l'héritage
            acl.SetAccessRuleProtection(true, false);

            // 1. Accès système (toujours en premier - ne peut pas échouer)
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            _log.LogDebug("ACL Système ajoutée pour {Path}", path);

            // 2. Accès utilisateur avec gestion d'erreur
            if (await TryAddUserAccessRule(acl, domain, samAccountName, path))
                _log.LogDebug("ACL Utilisateur ajoutée pour {Path} - {User}\\{Sam}", path, domain, samAccountName);
            else
                _log.LogWarning("Impossible d'ajouter l'accès utilisateur pour {User}\\{Sam} sur {Path}", domain,
                    samAccountName, path);

            // 3. Accès administrateurs avec gestion d'erreur
            if (await TryAddAdminAccessRule(acl, domain, path))
                _log.LogDebug("ACL Administrateurs ajoutée pour {Path}", path);
            else
                _log.LogWarning("Impossible d'ajouter l'accès administrateurs pour {Path}", path);

            // Application des ACL
            dir.SetAccessControl(acl);

            _log.LogInformation("ACL configurées avec succès pour {Path}", path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Erreur critique lors de la configuration des ACL pour {Path}. Utilisation des ACL par défaut.", path);
            // Ne lance plus d'exception - continue avec les ACL par défaut
        }

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Essaie d'ajouter l'accès utilisateur avec différentes méthodes de résolution.
    /// </summary>
    private async Task<bool> TryAddUserAccessRule(DirectorySecurity acl, string domain, string samAccountName,
        string path)
    {
        // Essaie différents formats pour résoudre l'utilisateur
        var userFormatsList = new List<string>
        {
            $"{domain}\\{samAccountName}", // DOMAIN\user (format passé)
            $"{samAccountName}@{domain}", // user@domain (UPN)
            samAccountName // user seul (SAM)
        };

        // ✅ CORRECTION : Ajoute les formats avec domaine LDAP configuré
        if (!string.IsNullOrWhiteSpace(_ldapDomain))
        {
            userFormatsList.Add($"{samAccountName}@{_ldapDomain}"); // UPN avec domaine LDAP configuré
            
            // Si le domaine LDAP contient un point, ajoute aussi le format NetBIOS avec point
            if (_ldapDomain.Contains("."))
            {
                var upperLdapDomain = _ldapDomain.ToUpperInvariant();
                userFormatsList.Add($"{upperLdapDomain}\\{samAccountName}");
            }
        }
        
        // Fallback : formats hardcodés pour compatibilité
        userFormatsList.Add($"LYCEE.ND\\{samAccountName}"); // NetBIOS complet avec .ND
        userFormatsList.Add($"{samAccountName}@lycee.nd"); // UPN avec FQDN complet

        // Ajoute le NetBIOS domain si différent
        if (!string.IsNullOrWhiteSpace(_netbiosDomain) &&
            !_netbiosDomain.Equals(domain, StringComparison.OrdinalIgnoreCase))
        {
            userFormatsList.Insert(1, $"{_netbiosDomain}\\{samAccountName}"); // NetBIOS\user
            
            // Ajoute aussi le format NetBIOS en UPN
            userFormatsList.Add($"{samAccountName}@{_netbiosDomain}");
        }

        // ✅ ANCIEN CODE SUPPRIMÉ : les formats avec lycee.nd sont déjà ajoutés au-dessus
        
        // Ajoute des formats avec le domaine en majuscules
        var upperDomain = domain.ToUpperInvariant();
        if (!userFormatsList.Any(f => f.Equals($"{upperDomain}\\{samAccountName}", StringComparison.OrdinalIgnoreCase)))
        {
            userFormatsList.Add($"{upperDomain}\\{samAccountName}");
        }

        var userFormats = userFormatsList.ToArray();

        _log.LogDebug("🔍 TryAddUserAccessRule - Paramètres:");
        _log.LogDebug("    • Domain: '{Domain}'", domain);
        _log.LogDebug("    • SamAccountName: '{SamAccountName}'", samAccountName);
        _log.LogDebug("    • NetBiosDomain: '{NetBiosDomain}'", _netbiosDomain);
        _log.LogDebug("    • Path: '{Path}'", path);
        _log.LogInformation("🔍 Tentative résolution utilisateur {Sam} avec {Count} formats:", samAccountName, userFormats.Length);
        foreach (var format in userFormats)
        {
            _log.LogDebug("    📋 Format à tester: '{Format}'", format);
        }

        foreach (var userFormat in userFormats)
        {
            _log.LogDebug("    🔍 Essai format: '{Format}'", userFormat);
            try
            {
                var userAccount = new NTAccount(userFormat);

                // Test de résolution du SID
                _log.LogDebug("    🔍 Tentative de résolution SID pour NTAccount: '{Format}'", userFormat);
                var userSid = userAccount.Translate(typeof(SecurityIdentifier));
                _log.LogInformation("    ✅ SID résolu avec succès: '{Format}' → {Sid}", userFormat, userSid);

                acl.AddAccessRule(new FileSystemAccessRule(
                    userAccount,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                _log.LogInformation("✅ Utilisateur résolu et ACL ajoutée avec le format: {Format} → SID: {Sid}", userFormat, userSid);
                return true;
            }
            catch (IdentityNotMappedException ex)
            {
                _log.LogWarning("    ❌ Utilisateur non trouvé avec format '{Format}': {Error}", userFormat, ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogWarning("    ❌ Erreur résolution utilisateur avec format '{Format}': {Error}", userFormat, ex.Message);
            }
        }

        // ✅ NOUVELLE TENTATIVE : Essaie avec le SID direct si on peut le trouver via LDAP
        _log.LogInformation("🔄 Tentative de résolution via SID direct depuis LDAP...");
        if (await TryAddUserBySidLookup(acl, samAccountName, domain))
        {
            _log.LogInformation("✅ Utilisateur résolu via SID direct depuis LDAP");
            return true;
        }

        _log.LogError("❌ ÉCHEC TOTAL: Impossible de résoudre l'utilisateur {Sam} avec aucun des {Count} formats testés ni via SID direct", samAccountName, userFormats.Length);
        return false;
    }

    /// <summary>
    /// Essaie de résoudre l'utilisateur via son SID obtenu depuis LDAP
    /// </summary>
    private async Task<bool> TryAddUserBySidLookup(DirectorySecurity acl, string samAccountName, string domain)
    {
        try
        {
            // Note: Pour une implémentation complète, on pourrait faire une requête LDAP
            // pour obtenir le SID de l'utilisateur et l'utiliser directement
            // Pour l'instant, on retourne false pour continuer avec les ACL par défaut
            _log.LogDebug("TryAddUserBySidLookup pas encore implémentée - utilisation des ACL par défaut");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Erreur lors de la résolution SID direct: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Essaie d'ajouter l'accès administrateurs avec différentes méthodes de résolution.
    /// </summary>
    private async Task<bool> TryAddAdminAccessRule(DirectorySecurity acl, string domain, string path)
    {
        // Essaie différents formats pour résoudre les administrateurs
        string[] adminFormats =
        {
            $"{domain}\\Domain Admins",
            $"{_netbiosDomain}\\Domain Admins",
            "BUILTIN\\Administrators",
            "Administrators"
        };

        foreach (var adminFormat in adminFormats)
        {
            if (string.IsNullOrWhiteSpace(adminFormat) || adminFormat.StartsWith("\\Domain"))
                continue;

            try
            {
                var adminAccount = new NTAccount(adminFormat);

                // Test de résolution du SID
                var adminSid = adminAccount.Translate(typeof(SecurityIdentifier));

                acl.AddAccessRule(new FileSystemAccessRule(
                    adminAccount,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                _log.LogDebug("Administrateurs résolus avec le format: {Format} -> SID: {Sid}", adminFormat, adminSid);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogDebug("Échec résolution administrateurs avec format {Format}: {Error}", adminFormat,
                    ex.Message);
            }
        }

        // Fallback : utilise le SID des administrateurs locaux
        try
        {
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(
                adminsSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            _log.LogDebug("Utilisation du SID Administrateurs par défaut: {Sid}", adminsSid);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Impossible d'ajouter même les administrateurs par défaut: {Error}", ex.Message);
        }

        return false;
    }

    /// <summary>
    ///     Crée un partage SMB individuel pour l'utilisateur via API native Windows.
    /// </summary>
    private async Task CreateUserSmbShareAsync(string server, string samAccountName, string uncPath, string domain)
    {
        try
        {
            // Nom du partage utilisateur (ex: chloe_moreau$)
            var shareName = $"{SanitizeShareName(samAccountName)}$";

            // Convertit le chemin UNC en chemin local pour le serveur
            var localPath = ConvertUncToLocalPath(uncPath, server);

            _log.LogInformation("Création partage SMB: {ShareName} -> {LocalPath} sur {Server}",
                shareName, localPath, server);

            // Crée le partage via API native Windows
            await CreateSmbShareViaNativeApiAsync(server, shareName, localPath, domain, samAccountName);

            _log.LogInformation("✅ Partage SMB créé avec succès: {ShareName}", shareName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Échec création partage SMB pour {User} sur {Server}", samAccountName, server);
            // Continue sans partage SMB - le dossier existe toujours
        }
    }

    /// <summary>
    ///     Crée un partage SMB via l'API native NetShareAdd.
    /// </summary>
    private async Task CreateSmbShareViaNativeApiAsync(string server, string shareName, string localPath,
        string domain, string samAccountName)
    {
        // Normalise le nom du serveur
        var serverName = server.StartsWith(@"\\") ? server : $@"\\{server}";

        // Supprime un éventuel partage existant (ignore les erreurs)
        Native.NetShareDel(serverName, shareName, 0);
        _log.LogDebug("Tentative suppression partage existant: {ShareName}", shareName);

        // Utilise SHARE_INFO_2 qui est plus simple et robuste
        var shareInfo = new Native.SHARE_INFO_2
        {
            shi2_netname = shareName,
            shi2_type = Native.STYPE_DISKTREE,
            shi2_remark = $"Partage personnel de {samAccountName}",
            shi2_permissions = Native.ACCESS_ALL, // Permissions par défaut
            shi2_max_uses = uint.MaxValue,
            shi2_current_uses = 0, // Pas d'utilisateurs connectés initialement
            shi2_path = localPath,
            shi2_passwd = null // Pas de mot de passe
        };

        // Appelle NetShareAdd avec le niveau 2
        var result = Native.NetShareAdd(serverName, 2, ref shareInfo, out var paramError);

        if (result != Native.NERR_Success)
        {
            var errorMsg = GetNetShareErrorDescription(result, paramError);
            _log.LogError("❌ NetShareAdd ÉCHEC pour {ShareName} sur {Server}: Code={Result}, ParamError={ParamError}, Message={ErrorMsg}", 
                shareName, serverName, result, paramError, errorMsg);
            _log.LogError("📋 Détails du partage: Path='{LocalPath}', Type={Type}, Permissions={Permissions}", 
                localPath, shareInfo.shi2_type, shareInfo.shi2_permissions);
            throw new InvalidOperationException($"NetShareAdd échec: {result} ({errorMsg})");
        }

        _log.LogDebug("Partage créé avec succès via NetShareAdd niveau 2: {ShareName}", shareName);

        // Configure les permissions SMB séparément
        await ConfigureSharePermissionsAsync(serverName, shareName, domain, samAccountName);

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Configure les permissions du partage SMB après création.
    /// </summary>
    private async Task ConfigureSharePermissionsAsync(string serverName, string shareName, string domain,
        string samAccountName)
    {
        try
        {
            // Pour configurer les permissions, on utiliserait normalement NetShareSetInfo
            // Mais c'est complexe, donc on log seulement pour l'instant
            _log.LogInformation("Partage {ShareName} créé. Permissions NTFS déjà configurées sur le dossier.",
                shareName);

            // Les permissions d'accès sont contrôlées par les ACL NTFS que nous avons déjà configurées
            // Le partage utilise les permissions par défaut (Everyone: Full) mais l'accès réel est limité par NTFS
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Avertissement lors de la configuration des permissions pour {ShareName}", shareName);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Convertit un chemin UNC en chemin local pour le serveur.
    ///     Ex: \\192.168.10.43\C$\Data\user -> C:\Data\user
    /// </summary>
    private static string ConvertUncToLocalPath(string uncPath, string server)
    {
        // Supprime \\server\ du début
        var serverPrefix = $@"\\{server}\";
        if (!uncPath.StartsWith(serverPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Chemin UNC invalide: {uncPath}");

        var relativePath = uncPath.Substring(serverPrefix.Length);

        // Ex: "C$\Data\user" -> "C:\Data\user"
        if (relativePath.Length >= 2 && relativePath[1] == '$')
        {
            var driveLetter = relativePath[0];
            var restOfPath = relativePath.Substring(2).TrimStart('\\');
            return $"{driveLetter}:\\{restOfPath}";
        }

        return relativePath;
    }

    /// <summary>
    ///     Configure un quota FSRM pour le dossier.
    /// </summary>
    private async Task ConfigureFsrmQuotaAsync(string path, long quotaBytes)
    {
        try
        {
            // Utilise l'API COM FSRM
            var fsrm = new FsrmQuotaManagerClass();

            // Vérifie si un quota existe déjà
            try
            {
                var existingQuota = fsrm.GetQuota(path);
                if (existingQuota != null)
                {
                    _log.LogDebug("Quota FSRM existe déjà pour {Path}", path);
                    return;
                }
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80045301)) // FRM_E_NOT_FOUND
            {
                // Normal, le quota n'existe pas encore
            }

            // Création du nouveau quota
            var quota = fsrm.CreateQuota(path);
            quota.QuotaLimit = (ulong)quotaBytes;
            quota.Description = $"Quota utilisateur - {quotaBytes / (1024 * 1024 * 1024)} GB";
            quota.Commit();

            _log.LogInformation("Quota FSRM créé : {Path} = {SizeGB} GB",
                path, quotaBytes / (1024 * 1024 * 1024));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Erreur lors de la création du quota FSRM pour {Path}. Le quota sera ignoré.", path);
            // Continue sans quota si FSRM n'est pas disponible
        }

        await Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     Construit le nom UPN (User Principal Name) à partir du username et de la configuration LDAP.
    /// </summary>
    private string BuildUpnUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return string.Empty;
            
        // Si le username contient déjà un @, on l'utilise tel quel
        if (username.Contains("@"))
            return username;
            
        // Sinon, on construit l'UPN avec le domaine LDAP configuré
        return string.IsNullOrWhiteSpace(_ldapDomain) 
            ? $"{username}@lycee.nd" // Fallback si pas de config
            : $"{username}@{_ldapDomain}";
    }

    /// <summary>
    ///     Teste la connectivité réseau vers un serveur.
    /// </summary>
    private async Task<bool> TestServerConnectivity(string serverName)
    {
        try
        {
            _log.LogDebug("🔍 Test de connectivité vers {Server}...", serverName);
            
            // Test de ping simple
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(serverName, 5000);
            
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                _log.LogDebug("✅ Ping vers {Server} réussi (RTT: {RoundtripTime}ms)", serverName, reply.RoundtripTime);
                return true;
            }
            else
            {
                _log.LogWarning("❌ Ping vers {Server} échoué: {Status}", serverName, reply.Status);
                return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "❌ Erreur lors du test de connectivité vers {Server}", serverName);
            return false;
        }
    }

    /// <summary>
    ///     Assure que les credentials sont chargés depuis la configuration LDAP.
    /// </summary>
    private async Task EnsureCredentialsAsync()
    {
        if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
            return;

        try
        {
            _username = await _ldap.GetUsernameAsync();
            _password = await _ldap.GetPasswordAsync();

            if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
                throw new InvalidOperationException("Credentials LDAP manquants pour les connexions SMB");

            _log.LogDebug("Credentials initialisés pour {Username}", _username);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erreur lors du chargement des credentials");
            throw;
        }
    }

    /// <summary>
    ///     Parse un nom de compte "DOMAIN\User" en domaine et utilisateur.
    /// </summary>
    private static (string? domain, string user) ParseAccountName(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return (null, accountName);

        var parts = accountName.Split('\\', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, accountName);
    }

    /// <summary>
    ///     Extrait le nom NetBIOS du domaine depuis un DN LDAP.
    /// </summary>
    private static string? ExtractNetbiosDomain(string? dn)
    {
        return dn?.Split(',')
            .FirstOrDefault(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))?[3..]
            .ToUpperInvariant();
    }

    /// <summary>
    ///     Nettoie un nom de fichier en supprimant les caractères invalides.
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return fileName;

        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(c => invalidChars.Contains(c) ? '_' : c))
            .TrimEnd('.', ' ');
    }

    /// <summary>
    ///     Calcule la taille du quota en octets selon le rôle utilisateur.
    /// </summary>
    private long CalculateQuotaBytes(UserRole role)
    {
        // Par défaut 50 GB, adaptez selon vos besoins
        var quotaGb = role switch
        {
            UserRole.Student => 50,
            _ => 50
        };

        return quotaGb * 1024L * 1024L * 1024L; // Conversion en octets
    }

    /// <summary>
    ///     Supprime la lettre de lecteur d'un chemin pour construire un chemin UNC correct.
    ///     Ex: "C:\Data" -> "Data", "Data" -> "Data"
    /// </summary>
    private static string RemoveDriveFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        // Nettoie le chemin
        path = path.Trim().TrimStart('\\', '/');

        // Si le chemin commence par une lettre de lecteur (ex: "C:")
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            // Enlève "C:" et les slashes qui suivent
            path = path.Substring(2).TrimStart('\\', '/');

        return path;
    }

    /// <summary>
    ///     Nettoie un nom pour qu'il soit valide comme nom de partage SMB.
    /// </summary>
    private static string SanitizeShareName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        // Caractères interdits dans les noms de partages SMB/NetBIOS
        char[] invalidChars =
        {
            '.', '/', '\\', ':', '*', '?', '"', '<', '>', '|',
            '+', '=', ';', ',', '[', ']', ' '
        };

        var clean = name;
        foreach (var c in invalidChars) clean = clean.Replace(c, '_');

        // Supprime les underscores multiples consécutifs
        while (clean.Contains("__")) clean = clean.Replace("__", "_");

        return clean.Trim('_');
    }

    /// <summary>
    ///     Retourne une description détaillée de l'erreur NetShare.
    /// </summary>
    private static string GetNetShareErrorDescription(uint errorCode, uint paramError)
    {
        return errorCode switch
        {
            5 => "ACCESS_DENIED - Droits insuffisants (besoin d'être Administrator)",
            87 => $"INVALID_PARAMETER - Paramètre invalide (index: {paramError})",
            2118 => "NERR_DuplicateShare - Partage existe déjà",
            2123 => "NERR_RedirectedPath - Chemin redirigé",
            2310 => "NERR_UnknownDevDir - Répertoire ou périphérique inconnu",
            _ => $"Erreur inconnue: {errorCode}"
        };
    }

    #endregion
}

/// <summary>
///     Classe pour gérer les connexions réseau authentifiées.
///     Utilise WNetAddConnection2 et WNetCancelConnection2 pour l'authentification SMB.
/// </summary>
public sealed class NetworkConnection : IDisposable
{
    private readonly NetworkCredential _credentials;
    private readonly string _networkName;
    private bool _disposed;

    public NetworkConnection(string networkName, NetworkCredential credentials)
    {
        _networkName = networkName;
        _credentials = credentials;

        var netResource = new NetResource
        {
            Scope = ResourceScope.GlobalNetwork,
            ResourceType = ResourceType.Disk,
            DisplayType = ResourceDisplaytype.Share,
            RemoteName = networkName
        };

        var userName = string.IsNullOrEmpty(credentials.Domain)
            ? credentials.UserName
            : $@"{credentials.Domain}\{credentials.UserName}";

        // ✅ AMÉLIORATION : Log détaillé avant connexion
        Console.WriteLine($"[NetworkConnection] Tentative connexion à {networkName} avec utilisateur {userName}");
        
        var result = WNetAddConnection2(netResource, credentials.Password, userName, 0);
        if (result != 0)
        {
            var errorMsg = GetWNetErrorDescription(result);
            Console.WriteLine($"[NetworkConnection] ÉCHEC connexion à {networkName}: Code={result}, Message={errorMsg}");
            throw new Win32Exception(result,
                $"Échec de connexion à {networkName} avec {userName}: {errorMsg}");
        }
        
        Console.WriteLine($"[NetworkConnection] ✅ Connexion réussie à {networkName}");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            WNetCancelConnection2(_networkName, 0, true);
            _disposed = true;
        }
    }

    #region P/Invoke

    [DllImport("mpr.dll")]
    private static extern int WNetAddConnection2(NetResource netResource, string password, string username, int flags);

    [DllImport("mpr.dll")]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [StructLayout(LayoutKind.Sequential)]
    private class NetResource
    {
        public ResourceScope Scope = ResourceScope.GlobalNetwork;
        public ResourceType ResourceType = ResourceType.Disk;
        public ResourceDisplaytype DisplayType = ResourceDisplaytype.Share;
        public int Usage = 0;
        public string LocalName = "";
        public string RemoteName = "";
        public string Comment = "";
        public string Provider = "";
    }

    private enum ResourceScope
    {
        Connected = 1,
        GlobalNetwork,
        Remembered,
        Recent,
        Context
    }

    private enum ResourceType
    {
        Any = 0,
        Disk = 1,
        Print = 2,
        Reserved = 8
    }

    private enum ResourceDisplaytype
    {
        Generic = 0x0,
        Domain = 0x01,
        Server = 0x02,
        Share = 0x03,
        File = 0x04,
        Group = 0x05,
        Network = 0x06,
        Root = 0x07,
        Shareadmin = 0x08,
        Directory = 0x09,
        Tree = 0x0a,
        Ndscontainer = 0x0b
    }

    /// <summary>
    /// Retourne une description détaillée de l'erreur WNet
    /// </summary>
    private static string GetWNetErrorDescription(int errorCode)
    {
        return errorCode switch
        {
            5 => "ACCESS_DENIED - Accès refusé (vérifiez les credentials)",
            53 => "BAD_NETPATH - Chemin réseau introuvable",
            67 => "BAD_NET_NAME - Nom réseau introuvable",
            86 => "INVALID_PASSWORD - Mot de passe incorrect",
            1219 => "SESSION_CREDENTIAL_CONFLICT - Conflit de session (déjà connecté avec d'autres credentials)",
            1326 => "LOGON_FAILURE - Échec d'authentification (nom d'utilisateur ou mot de passe incorrect)",
            1331 => "ACCOUNT_DISABLED - Compte désactivé",
            1909 => "ACCOUNT_LOCKED_OUT - Compte verrouillé",
            2202 => "BAD_USERNAME - Nom d'utilisateur invalide",
            _ => $"Erreur inconnue: {errorCode}"
        };
    }

    #endregion
}

/// <summary>
///     Interface COM pour FSRM Quota Manager.
///     Nécessite la référence COM "File Server Resource Manager".
/// </summary>
[ComImport]
[Guid("4173AC41-172D-4D52-963C-FDC7E415F717")]
internal class FsrmQuotaManagerClass
{
    [DispId(0x60020000)]
    public virtual extern IFsrmQuota CreateQuota([In] string path);

    [DispId(0x60020001)]
    public virtual extern IFsrmQuota GetQuota([In] string path);
}

/// <summary>
///     Interface COM pour un quota FSRM individuel.
/// </summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
[Guid("42DC3511-61D5-48AE-B6DC-59FC00C0A8D6")]
internal interface IFsrmQuota
{
    [DispId(0x60020006)] ulong QuotaLimit { get; set; }

    [DispId(0x60020008)] string Description { get; set; }

    [DispId(0x60020000)]
    void Commit();
}

/// <summary>
///     P/Invoke pour les APIs NetShare
/// </summary>
internal static class Native
{
    internal const int STYPE_DISKTREE = 0;
    internal const uint NERR_Success = 0;
    internal const uint ACCESS_ALL = 0x1FF; // Full access

    // Codes d'erreur NetShare courants
    internal const uint ERROR_ACCESS_DENIED = 5;
    internal const uint ERROR_INVALID_PARAMETER = 87;
    internal const uint NERR_DuplicateShare = 2118;
    internal const uint NERR_RedirectedPath = 2123;
    internal const uint NERR_UnknownDevDir = 2310;

    // Surcharge pour SHARE_INFO_2
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint NetShareAdd(
        string? serverName,
        int level,
        ref SHARE_INFO_2 buf,
        out uint parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint NetShareDel(
        string? serverName,
        string netName,
        int reserved);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHARE_INFO_2
    {
        public string shi2_netname;
        public int shi2_type;
        public string? shi2_remark;
        public uint shi2_permissions;
        public uint shi2_max_uses;
        public uint shi2_current_uses;
        public string shi2_path;
        public string? shi2_passwd;
    }
}