using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ADManagerAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ActiveDirectoryController : ControllerBase
{
    private readonly ILdapService _ldap;
    private readonly ILogger<ActiveDirectoryController> _log;

    public ActiveDirectoryController(ILdapService ldap,
        ILogger<ActiveDirectoryController> log)
    {
        _ldap = ldap;
        _log = log;
    }

 
    [HttpGet("health")]
    public async Task<ActionResult> GetHealth()
    {
        try
        {
            var ok = await _ldap.TestConnectionAsync();
            var stat = _ldap.GetHealthStatus();
            return Ok(new { isHealthy = ok, details = stat });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Health-check LDAP failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }


    [HttpGet("root")]
    public async Task<ActionResult<List<ActiveDirectoryNodeDto>>> GetRoot()
    {
        try
        {
            if (!await _ldap.TestConnectionAsync())
                return StatusCode(503, new { error = "LDAP unavailable" });

            var baseDn = await GetBaseDnAsync();
            
            // Retourner directement les enfants du domaine racine
            var children = await BuildChildren(baseDn);
            return Ok(children);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetRoot failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

  
    [HttpGet("children")]
    public async Task<ActionResult<List<ActiveDirectoryNodeDto>>> GetChildren(
        [FromQuery] string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
            return BadRequest(new { error = "DistinguishedName requis" });

        if (!await _ldap.TestConnectionAsync())
            return StatusCode(503, new { error = "LDAP unavailable" });

        try
        {
            var children = await BuildChildren(distinguishedName);
            return Ok(children);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetChildren failed for {DN}", distinguishedName);
            return StatusCode(500, new { error = ex.Message });
        }
    }

   
    [HttpGet("search")]
    public async Task<ActionResult<List<ActiveDirectoryNodeDto>>> Search(
        [FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "Query requis" });

        if (!await _ldap.TestConnectionAsync())
            return StatusCode(503, new { error = "LDAP unavailable" });

        try
        {
            return Ok(await SearchUsers(query));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Search failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("bulkAction")]
    public async Task<ActionResult<BulkActionResultDto>> BulkAction(
        [FromBody] BulkActionRequestDto req)
    {
        if (req?.Users is null || req.Users.Count == 0)
            return BadRequest(new { error = "Pas d'utilisateurs" });

        if (!await _ldap.TestConnectionAsync())
            return StatusCode(503, new { error = "LDAP unavailable" });

        try
        {
            var result = await ExecuteBulk(req);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "BulkAction failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("debug/{*distinguishedName}")]
    public async Task<ActionResult> DebugOU([FromRoute] string distinguishedName)
    {
        try
        {
            if (!await _ldap.TestConnectionAsync())
                return StatusCode(503, new { error = "LDAP unavailable" });

            _log.LogInformation("🔍 DEBUG OU: {DN}", distinguishedName);
            
            var containers = await _ldap.GetContainersAsync(distinguishedName);
            var ous = await _ldap.GetOrganizationalUnitsAsync(distinguishedName);
            var users = await _ldap.GetUsersAsync(distinguishedName, maxResults: 100);
            
            return Ok(new
            {
                distinguishedName,
                containers = containers.Select(c => new { c.Name, c.DistinguishedName }),
                organizationalUnits = ous.Select(ou => new { ou.Name, ou.DistinguishedName }),
                users = users.Select(u => new { 
                    u.SamAccountName, 
                    u.DisplayName, 
                    DistinguishedName = u.AdditionalAttributes.GetValueOrDefault("distinguishedName", "VIDE!"),
                    HasDN = !string.IsNullOrEmpty(u.AdditionalAttributes.GetValueOrDefault("distinguishedName"))
                }),
                summary = new
                {
                    containersCount = containers.Count,
                    ousCount = ous.Count,
                    usersCount = users.Count,
                    usersWithValidDN = users.Count(u => !string.IsNullOrEmpty(u.AdditionalAttributes.GetValueOrDefault("distinguishedName")))
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Debug failed for {DN}", distinguishedName);
            return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
        }
    }

    /*==================================================================*/
    /*                           Helpers                                */
    /*==================================================================*/

    private async Task<string> GetBaseDnAsync()
    {
        var h = _ldap.GetHealthStatus();
        
        // Si le BaseDn n'est pas configuré, essayer d'initialiser la connexion
        if (string.IsNullOrWhiteSpace(h.BaseDn) || h.BaseDn == "Non configuré")
        {
            // Forcer l'initialisation en appelant TestConnection qui va déclencher EnsureConnectionAsync
            await _ldap.TestConnectionAsync();
            h = _ldap.GetHealthStatus();
            
            if (string.IsNullOrWhiteSpace(h.BaseDn) || h.BaseDn == "Non configuré")
                throw new InvalidOperationException("Base DN non configuré");
        }
        
        return h.BaseDn;
    }
    
    private string GetBaseDn()
    {
        return GetBaseDnAsync().GetAwaiter().GetResult();
    }

    private static string DnToDomain(string dn)
    {
        return string.Join('.', dn.Split(',')
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..]));
    }

    /*------------------------------------------------------------------*/
    private async Task<List<ActiveDirectoryNodeDto>> BuildChildren(string parentDn)
    {
        var list = new List<ActiveDirectoryNodeDto>();
        
        _log.LogDebug("🔍 BuildChildren pour {ParentDn}", parentDn);

   
        var containers = await _ldap.GetContainersAsync(parentDn);
        _log.LogDebug("📁 {Count} conteneurs trouvés", containers.Count);
        list.AddRange(containers.Select(c => new ActiveDirectoryNodeDto
        {
            Name = c.Name ?? Extract(c.DistinguishedName),
            DistinguishedName = c.DistinguishedName,
            HasChildren = true,
            ObjectClasses = new[] { "container" }
        }));

        var ous = await _ldap.GetOrganizationalUnitsAsync(parentDn);
        _log.LogDebug("🏢 {Count} OUs trouvées", ous.Count);
        list.AddRange(ous.Select(ou => new ActiveDirectoryNodeDto
        {
            Name = ou.Name ?? Extract(ou.DistinguishedName),
            DistinguishedName = ou.DistinguishedName,
            HasChildren = true,
            ObjectClasses = new[] { "organizationalUnit" }
        }));

   
        var users = await _ldap.GetUsersAsync(parentDn, maxResults: 20);
        var validUsers = users.Where(u => !string.IsNullOrEmpty(u.AdditionalAttributes.GetValueOrDefault("distinguishedName"))).ToList();
        _log.LogDebug("👥 {ValidCount}/{TotalCount} utilisateurs valides trouvés", validUsers.Count, users.Count);
        
        list.AddRange(validUsers.Select(u => new ActiveDirectoryNodeDto
        {
            Name = FormatUserName(u),
            DistinguishedName = u.AdditionalAttributes.GetValueOrDefault("distinguishedName") ?? "",
            HasChildren = false,
            ObjectClasses = new[] { "user" },
            UserAccountControl = ParseUac(u),
            Email = u.AdditionalAttributes.GetValueOrDefault("mail"),
            Description = u.AdditionalAttributes.GetValueOrDefault("description")
        }));

        var totalCount = list.Count;
        _log.LogDebug("✅ Total: {TotalCount} éléments ({Containers} conteneurs, {OUs} OUs, {Users} utilisateurs)", 
            totalCount, containers.Count, ous.Count, validUsers.Count);

        return list.OrderBy(n => n.ObjectClasses[0] == "user" ? 1 : 0) // OUs/Containers avant les utilisateurs
                  .ThenBy(n => n.Name)
                  .ToList();
    }

    /*------------------------------------------------------------------*/
    private async Task<List<ActiveDirectoryNodeDto>> SearchUsers(string q)
    {
        var users = await _ldap.SearchUsersAsync(GetBaseDn(),
            $"(&(objectClass=user)(|(cn=*{q}*)(sAMAccountName=*{q}*)(mail=*{q}*)))");

        return users.Where(u => !string.IsNullOrEmpty(u.AdditionalAttributes.GetValueOrDefault("distinguishedName")))
            .Take(50).Select(u => new ActiveDirectoryNodeDto
            {
                Name = u.DisplayName ?? $"{u.GivenName} {u.Surname}".Trim(),
                DistinguishedName = u.AdditionalAttributes.GetValueOrDefault("distinguishedName") ?? "",
                HasChildren = false,
                ObjectClasses = new[] { "user" },
                UserAccountControl = ParseUac(u),
                Email = u.AdditionalAttributes.GetValueOrDefault("mail"),
                Description = u.AdditionalAttributes.GetValueOrDefault("description")
            }).ToList();
    }

    /*------------------------------------------------------------------*/
    private async Task<BulkActionResultDto> ExecuteBulk(BulkActionRequestDto r)
    {
        var res = new BulkActionResultDto
        {
            Action = r.Action,
            TotalCount = r.Users.Count,
            SuccessCount = 0,
            FailureCount = 0,
            Results = new List<BulkActionItemResultDto>()
        };

        foreach (var dn in r.Users)
            try
            {
                await _ldap.DoBulkActionAsync(dn, r);
                res.Results.Add(new BulkActionItemResultDto
                {
                    UserDistinguishedName = dn,
                    Success = true,
                    Message = "OK"
                });
                res.SuccessCount++;
            }
            catch (Exception ex)
            {
                res.Results.Add(new BulkActionItemResultDto
                {
                    UserDistinguishedName = dn,
                    Success = false,
                    Message = ex.Message
                });
                res.FailureCount++;
            }

        return res;
    }

    /*------------------------------------------------------------------*/
    private static string Extract(string dn)
    {
        return dn.Split(',')[0][3..];
    }

    private static int? ParseUac(UserModel u)
    {
        return int.TryParse(u.AdditionalAttributes.GetValueOrDefault("userAccountControl"), out var v) ? v : null;
    }
    
    private static string FormatUserName(UserModel u)
    {
        // Priorité : DisplayName > "Prénom Nom" > SamAccountName
        if (!string.IsNullOrWhiteSpace(u.DisplayName))
            return u.DisplayName;
            
        var fullName = $"{u.GivenName} {u.Surname}".Trim();
        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName;
            
        return u.SamAccountName;
    }


    #region DTOs

    public class ActiveDirectoryNodeDto
    {
        public string Name { get; set; } = "";
        public string DistinguishedName { get; set; } = "";
        public bool HasChildren { get; set; }
        public string[] ObjectClasses { get; set; } = Array.Empty<string>();
        public int? UserAccountControl { get; set; }
        public string? LastLogon { get; set; }
        public string? Description { get; set; }
        public string? Email { get; set; }
    }

    public class BulkActionRequestDto
    {
        public string Action { get; set; } = "";
        public List<string> Users { get; set; } = new();
        public string? NewPassword { get; set; }
        public string? Description { get; set; }
        public string? TargetOU { get; set; }
    }

    public class BulkActionResultDto
    {
        public string Action { get; set; } = "";
        public int TotalCount { get; set; }
        public double SuccessCount { get; set; }
        public double FailureCount { get; set; }
        public List<BulkActionItemResultDto> Results { get; set; } = new();
    }

    public class BulkActionItemResultDto
    {
        public string UserDistinguishedName { get; set; } = "";
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    #endregion
}