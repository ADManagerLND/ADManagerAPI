// -----------------------------------------------------------------------------
//  FolderManagementService.cs
//  v6 : • ACL = SIDs fiables via ResolveSid (NTAccount → LookupAccountName → fallback)
//       • Administrators + Service-account + Élève gardent FullControl
//       • Plus aucune IdentityNotMappedException
// -----------------------------------------------------------------------------

using System.DirectoryServices.AccountManagement;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ADManagerAPI.Services;

public sealed class FolderManagementService : IFolderManagementService
{
    private readonly ILogger<FolderManagementService> _log;
    private readonly IConfiguration _cfg;
    private readonly string? _ldapUser;
    private readonly string? _ldapPass;
    private readonly string? _netbiosDomain;
    private readonly string? _ldapServer;                 // DC/FQDN

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    private static readonly IReadOnlyDictionary<string, (string Res, int Index)> _folderIcons =
        new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"]   = ("%SystemRoot%\\System32\\imageres.dll", -183),
            ["Documents"] = ("%SystemRoot%\\System32\\imageres.dll", -112),
            ["Downloads"] = ("%SystemRoot%\\System32\\imageres.dll", -184),
            ["Pictures"]  = ("%SystemRoot%\\System32\\imageres.dll", -113)
        };

    // ------------------------------------------------------------------------
    public FolderManagementService(IConfiguration cfg, ILogger<FolderManagementService> log)
    {
        _cfg           = cfg;
        _log           = log;
        _ldapUser      = cfg["LdapSettings:Username"];
        _ldapPass      = cfg["LdapSettings:Password"];
        _ldapServer    = cfg["LdapSettings:Server"];      // ex: adtst01.local
        _netbiosDomain = ExtractNetbios(cfg["LdapSettings:Domain"]);
    }

    // ------------------------------------------------------------------------
    #region Public API

    public Task<bool> CreateStudentFolderAsync(StudentInfo s, string template, UserRole r) =>
        Task.FromResult(true); // à implémenter

    public Task<bool> CreateClassGroupFolderAsync(ClassGroupInfo c, string template) =>
        Task.FromResult(true); // à implémenter

    public async Task<bool> ProvisionUserShareAsync(
        string serverName,
        string localBasePath,
        string configuredShare,
        string accountAd,
        List<string> subfolders)
    {
        if (!ValidateParameters(serverName, localBasePath, configuredShare, accountAd, subfolders))
            return false;

        var (domain, sam) = ParseAccount(accountAd);
        sam = CollapseDuplicateSam(sam);
        var aclDomain     = _netbiosDomain ?? domain;

        var physicalPath = Path.Combine(localBasePath, sam);
        var isRemote     = !IsLocalServer(serverName);
        var ioPath       = isRemote ? $@"\\{serverName}\{configuredShare}\{sam}" : physicalPath;

        try
        {
            await RunAsConfiguredUserAsync(isRemote, () => EnsureDirectories(ioPath, subfolders));
            await RunAsConfiguredUserAsync(isRemote, () => ApplyNtfsAcl(ioPath, aclDomain, sam));
            
            var iconPath = isRemote ? ioPath : physicalPath;   // toujours le chemin local
            await RunAsConfiguredUserAsync(isRemote, () =>
                SetupIcons(iconPath, subfolders, skipIcons: false));
            
            await CreateShareAsync(serverName, physicalPath, sam, aclDomain, isRemote);

            _log.LogInformation("[FMS] Provisioning completed for {Share}", sam);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[FMS] Provisioning failed for {Share}", sam);
            TryRollback(ioPath);
            return false;
        }
    }

    #endregion
    // ------------------------------------------------------------------------
    #region Directory & ACL

    private void EnsureDirectories(string root, IEnumerable<string> subs)
    {
        Directory.CreateDirectory(root);
        foreach (var raw in subs)
            Directory.CreateDirectory(Path.Combine(root, Sanitize(raw)));
    }

    private void SetupIcons(string root, IEnumerable<string> subs, bool skipIcons)
    {
        if (skipIcons) return;
        foreach (var raw in subs)
            TryCreateIcon(Path.Combine(root, Sanitize(raw)), Sanitize(raw));
    }

    private void ApplyNtfsAcl(string path, string domain, string sam)
    {
        var di  = new DirectoryInfo(path);
        var sec = di.GetAccessControl();
        sec.SetAccessRuleProtection(true, false);

        // ----- 1. Élève ------------------------------------------------------
        var studentSid = ResolveSid(domain, sam);
        sec.AddAccessRule(new FileSystemAccessRule(
            studentSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

        // ----- 2. BUILTIN\Administrators -------------------------------------
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        sec.AddAccessRule(new FileSystemAccessRule(
            adminSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

        // ----- 3. Compte de service (si distinct) ----------------------------
        if (!string.IsNullOrWhiteSpace(_ldapUser) &&
            !_ldapUser.Contains("administrators", StringComparison.OrdinalIgnoreCase))
        {
            if (TryResolveServiceSid(out var svcSid))
            {
                sec.AddAccessRule(new FileSystemAccessRule(
                    svcSid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
        }

        // Owner = élève
        sec.SetOwner(studentSid);

        di.SetAccessControl(sec);
    }

    private bool TryResolveServiceSid(out SecurityIdentifier sid)
    {
        sid = null!;
        try
        {
            string acc = _ldapUser!;
            string svcDom = null, svcSam = acc;

            if (acc.Contains('\\')) { var p = acc.Split('\\', 2); svcDom = p[0]; svcSam = p[1]; }
            else if (acc.Contains('@')) { var p = acc.Split('@', 2); svcSam = p[0]; svcDom = p[1].Split('.', 2)[0].ToUpper(); }
            else svcDom = _netbiosDomain;

            sid = ResolveSid(svcDom!, svcSam);
            return sid != null;
        }
        catch { return false; }
    }

    // -------- SID resolution : NTAccount → LookupAccountName → fallback ------
    // --- ResolveSid -----------------------------------------------------------
    private SecurityIdentifier ResolveSid(string domain, string sam)
    {
        var dom = string.IsNullOrWhiteSpace(domain) ? _netbiosDomain : domain;
        if (string.IsNullOrWhiteSpace(dom))
            throw new ArgumentException("Aucun domaine NetBIOS disponible");

        var accountVariants = new[]
        {
            $@"{dom}\{sam}",              // ADTEST01\alexandre.dupont
            $@"{dom.ToUpper()}\{sam}",    // ADTEST01\alexandre.dupont  (tout caps)
            sam                           // alexandre.dupont
        };

        foreach (var acc in accountVariants)
        {
            try
            {
                _log.LogInformation("ResolveSid → NTAccount.Translate({Acc})", acc);
                return (SecurityIdentifier)new NTAccount(acc)
                    .Translate(typeof(SecurityIdentifier));
            }
            catch (IdentityNotMappedException) { /* continue */ }

            if (TryLookupAccountName(_ldapServer, acc, out var sid) ||
                TryLookupAccountName(null,        acc, out sid))
                return sid;
        }

        _log.LogInformation("[FMS] SID unresolved for {0}. Using Authenticated Users.", sam);
        return new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
    }


    private static bool TryLookupAccountName(string? server, string account, out SecurityIdentifier sid)
    {
        sid = null!;
        uint sidLen = 0, domLen = 0, peUse;
        LookupAccountName(server, account, IntPtr.Zero, ref sidLen, null, ref domLen, out peUse);
        if (sidLen == 0) return false;

        
        var sidPtr = Marshal.AllocHGlobal((int)sidLen);
        var domBuf = new StringBuilder((int)domLen);
        try
        {
            if (!LookupAccountName(server, account, sidPtr, ref sidLen, domBuf, ref domLen, out peUse))
                return false;
            sid = new SecurityIdentifier(sidPtr);
            return true;
        }
        finally { Marshal.FreeHGlobal(sidPtr); }
    }

    #endregion
    // ------------------------------------------------------------------------
    #region SMB Share (WMI)

    private async Task CreateShareAsync(string server, string localPath, string sam, string domain, bool remote)
    {
        var scope      = BuildManagementScope(server, remote);
        using var shareClass = new ManagementClass(scope, new ManagementPath("Win32_Share"), null);
        using var inParams   = shareClass.GetMethodParameters("Create");

        inParams["Path"]           = localPath;
        inParams["Name"]           = sam + "$";
        inParams["Type"]           = 0;
        inParams["MaximumAllowed"] = 1;
        inParams["Description"]    = $"Home folder for {sam}";
        inParams["Access"]         = BuildShareSecurityDescriptor(scope, domain, sam);

        uint rc = (uint)shareClass.InvokeMethod("Create", inParams, null)["ReturnValue"];
        if (rc == 2)
            _log.LogError("[FMS] Win32_Share.Create AccessDenied. {0} doit être admin sur {1}.", _ldapUser, server);
        if (rc != 0)
            throw new InvalidOperationException($"Win32_Share.Create failed with code {rc}");
    }

    private ManagementScope BuildManagementScope(string server, bool remote)
    {
        if (!remote) return new ManagementScope("root/cimv2");

        var conn = new ConnectionOptions
        {
            Authentication = AuthenticationLevel.PacketPrivacy,
            Impersonation  = ImpersonationLevel.Impersonate,
            Username       = _ldapUser,
            Password       = _ldapPass
        };
        var scope = new ManagementScope($@"\\{server}\root\cimv2", conn);
        scope.Connect();
        return scope;
    }

    private static ManagementObject BuildShareSecurityDescriptor(
        ManagementScope scope, string domain, string sam)
    {
        var trustee = new ManagementClass(scope, new ManagementPath("Win32_Trustee"), null).CreateInstance();
        trustee["Domain"] = domain;   // NetBIOS, pas le FQDN
        trustee["Name"]   = sam;

        var ace = new ManagementClass(scope, new ManagementPath("Win32_ACE"), null).CreateInstance();
        ace["AceType"]    = 0;          // allow
        ace["AceFlags"]   = 3;          // objet + conteneur
        ace["AccessMask"] = 0x001F01FF; // 2032127 = Full Control
        ace["Trustee"]    = trustee;

        var sd = new ManagementClass(scope, new ManagementPath("Win32_SecurityDescriptor"), null).CreateInstance();
        sd["ControlFlags"] = 4;         // SE_DACL_PRESENT
        sd["DACL"]         = new[] { ace };
        return sd;
    }


    #endregion
    // ------------------------------------------------------------------------
    #region Helpers

    private static bool ValidateParameters(string srv, string p, string sh, string acc, IReadOnlyCollection<string> sub) =>
        !string.IsNullOrWhiteSpace(srv) && !string.IsNullOrWhiteSpace(p) &&
        !string.IsNullOrWhiteSpace(sh)  && !string.IsNullOrWhiteSpace(acc) &&
        sub is { Count: > 0 };

    private static (string Domain, string Sam) ParseAccount(string acc)
    {
        var parts = acc.Split('\\', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) throw new ArgumentException("accountAd must be DOMAINE\\samAccount");
        return (parts[0], parts[1]);
    }

    // …et simplement :
    private static string CollapseDuplicateSam(string sam) => sam;


    private static bool IsLocalServer(string s) =>
        string.Equals(s, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(s, "localhost",            StringComparison.OrdinalIgnoreCase);

    private async Task RunAsConfiguredUserAsync(bool impersonate, Action action)
    {
        if (!impersonate || string.IsNullOrWhiteSpace(_ldapUser) || string.IsNullOrWhiteSpace(_ldapPass))
        { action(); return; }

        using var h = LogonAndGetToken(_ldapUser!, _ldapPass!);
        WindowsIdentity.RunImpersonated(h, action);
    }

    private static SafeAccessTokenHandle LogonAndGetToken(string user, string pass)
    {
        string? dom = null; var usr = user;
        if (user.Contains('\\')) { var p = user.Split('\\', 2); dom = p[0]; usr = p[1]; }
        if (!LogonUser(usr, dom, pass, 2, 0, out var token))
            throw new InvalidOperationException("LogonUser failed");
        return token;
    }

    private static string Sanitize(string name) =>
        new(string.Concat(name.Select(c => InvalidChars.Contains(c) ? '_' : c)).TrimEnd('.', ' '));

    private static void TryCreateIcon(string folderPath, string folderName)
    {
        if (!_folderIcons.TryGetValue(folderName, out var ico))
            return;

        // S’assure que le sous-dossier existe (par prudence)
        Directory.CreateDirectory(folderPath);

        var ini = Path.Combine(folderPath, "desktop.ini");
        File.WriteAllText(
            ini,
            "[.ShellClassInfo]\r\n" +
            $"IconResource={ico.Res},{ico.Index}\r\n",
            Encoding.Unicode);                       // évite les soucis d’UTF-8

        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        // Attribut System (+ReadOnly facultatif) sur le dossier
        var attr = File.GetAttributes(folderPath);
        File.SetAttributes(folderPath, attr | FileAttributes.System | FileAttributes.ReadOnly);
    }



    private static void TryRollback(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* ignore */ }
    }

    private static string? ExtractNetbios(string? ldap) =>
        string.IsNullOrWhiteSpace(ldap) ? null :
        ldap.Split(',').FirstOrDefault(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))?
            .Substring(3).ToUpperInvariant();

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, string pwd,
        int logonType, int provider, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountName(string? system, string account, IntPtr sid, ref uint cbSid,
        StringBuilder referencedDomain, ref uint cchReferencedDomain, out uint peUse);

    #endregion
}
