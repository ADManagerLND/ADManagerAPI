using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ADManagerAPI.Hubs;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Services.Parse;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;

namespace ADManagerAPI.Services;

public record TemplateToken(string FullMatch, string ColumnName, string? Modifier);

public partial class SpreadsheetImportService : ISpreadsheetImportService
{
    /// <summary>
    ///     Cache pour les templates déjà traités - améliore les performances pour les imports volumineux
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> _templateRegexCache = new();

    private static readonly ConcurrentDictionary<string, List<TemplateToken>> _templateTokenCache = new();


    private readonly IFolderManagementService _folderManagementService;
    private readonly IHubContext<CsvImportHub>? _hubContext;
    private readonly ILdapService _ldapService;
    private readonly ILogger<SpreadsheetImportService> _logger;
    private readonly ILogService _logService;
    private readonly IConfiguration _configuration;
    private readonly IConfigService _configService;

    private readonly IEnumerable<ISpreadsheetDataParser> _parsers;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ITeamsIntegrationService? _teamsIntegrationService;

    public SpreadsheetImportService(
        IEnumerable<ISpreadsheetDataParser> parsers,
        ILdapService ldapService,
        ILogService logService,
        ILogger<SpreadsheetImportService> logger,
        IServiceScopeFactory serviceScopeFactory,
        IFolderManagementService folderManagementService,
        IConfiguration configuration,
        IConfigService configService,
        IHubContext<CsvImportHub>? hubContext = null,
        ITeamsIntegrationService? teamsIntegrationService = null)
    {
        _parsers = parsers ?? throw new ArgumentNullException(nameof(parsers));
        _ldapService = ldapService ?? throw new ArgumentNullException(nameof(ldapService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _folderManagementService =
            folderManagementService ?? throw new ArgumentNullException(nameof(folderManagementService));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _hubContext = hubContext;
        _teamsIntegrationService = teamsIntegrationService;
    }

    private ISpreadsheetDataParser? ChooseParser(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return _parsers.FirstOrDefault(p => p.CanHandle(extension));
    }

    private async Task<bool> CheckOrganizationalUnitExistsAsync(string ouPath)
    {
        if (string.IsNullOrEmpty(ouPath))
            return false;

        return await _ldapService.OrganizationalUnitExistsAsync(ouPath);
    }
}