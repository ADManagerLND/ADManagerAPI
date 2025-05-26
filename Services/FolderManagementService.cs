using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ADManagerAPI.Utils; // Added for AsyncLazy

namespace ADManagerAPI.Services
{
    public class FolderManagementService : IFolderManagementService
    {
        private readonly AsyncLazy<FolderManagementSettings> _folderSettingsLazy;
        private readonly AsyncLazy<FsrmSettings> _fsrmSettingsLazy;
        private readonly IConfigService _configService;
        private readonly ILogger<FolderManagementService> _logger;
        // private readonly IToolbelt _toolbelt; // Will be added if run_terminal_cmd is available

        public FolderManagementService(IConfigService configService, ILogger<FolderManagementService> logger /*, IToolbelt toolbelt*/)
        {
            _configService = configService;
            _logger = logger;
            // _toolbelt = toolbelt; // Will be assigned if run_terminal_cmd is available

            _folderSettingsLazy = new AsyncLazy<FolderManagementSettings>(async () => 
            {
                var settings = await _configService.GetFolderManagementSettingsAsync();
                if (settings == null)
                {
                    _logger.LogWarning("FolderManagementSettings could not be loaded. Using default settings.");
                    return new FolderManagementSettings 
                    {
                        BaseStudentPath = "./Students_Default", 
                        BaseClassGroupPath = "./Classes_Default",
                        Templates = new List<FolderTemplate>()
                    };
                }
                return settings;
            });

            _fsrmSettingsLazy = new AsyncLazy<FsrmSettings>(async () => 
            {
                var settings = await _configService.GetFsrmSettingsAsync();
                if (settings == null) 
                {
                    _logger.LogWarning("FsrmSettings could not be loaded. FSRM quotas will be disabled by default.");
                    return new FsrmSettings { EnableFsrmQuotas = false };
                }
                return settings;
            });
        }

        private async Task<FolderManagementSettings> GetFolderSettingsAsync()
        {
            return await _folderSettingsLazy.Value;
        }

        private async Task<FsrmSettings> GetFsrmSettingsAsync()
        {
            return await _fsrmSettingsLazy.Value;
        }

        // Updated signature to include UserRole
        public async Task<bool> CreateStudentFolderAsync(StudentInfo student, string templateName, UserRole role)
        {
            var folderSettings = await GetFolderSettingsAsync();
            var template = folderSettings.Templates?.FirstOrDefault(t => t.Name == templateName && t.Type == TemplateType.Student);
            if (template == null)
            {
                _logger.LogError($"Student folder template '{templateName}' not found.");
                return false;
            }

            var basePath = Path.Combine(folderSettings.BaseStudentPath, SanitizeFolderName(student.Name + "_" + student.Id));
            bool folderCreated = await CreateFoldersFromTemplateAsync(basePath, template, student);

            if (folderCreated)
            {
                var fsrmSettings = await GetFsrmSettingsAsync();
                if (fsrmSettings.EnableFsrmQuotas)
                {
                    if (fsrmSettings.QuotaTemplatesByRole != null && fsrmSettings.QuotaTemplatesByRole.TryGetValue(role, out var quotaTemplate))
                    {
                        if (!string.IsNullOrWhiteSpace(quotaTemplate))
                        {
                            await ApplyFsrmQuotaAsync(basePath, quotaTemplate, fsrmSettings.FsrmServerName);
                        }
                        else
                        {
                            _logger.LogWarning($"FSRM quota template for role '{role}' is configured but empty. Quota not applied to {basePath}.");
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"No FSRM quota template defined for role '{role}'. Quota not applied to {basePath}.");
                    }
                }
            }
            return folderCreated;
        }

        public async Task<bool> CreateClassGroupFolderAsync(ClassGroupInfo classGroup, string templateName)
        {
            var folderSettings = await GetFolderSettingsAsync();
            var template = folderSettings.Templates?.FirstOrDefault(t => t.Name == templateName && t.Type == TemplateType.ClassGroup);
            if (template == null)
            {
                _logger.LogError($"Class group folder template '{templateName}' not found.");
                return false;
            }

            var basePath = Path.Combine(folderSettings.BaseClassGroupPath, SanitizeFolderName(classGroup.Name + "_" + classGroup.Id));
            // Note: FSRM quota logic is not currently applied to ClassGroup folders in this example.
            // You could extend it similarly to CreateStudentFolderAsync if needed.
            return await CreateFoldersFromTemplateAsync(basePath, template, classGroup);
        }

        private async Task ApplyFsrmQuotaAsync(string path, string quotaTemplateName, string? fsrmServerName)
        {
            _logger.LogInformation($"Attempting to apply FSRM quota template '{quotaTemplateName}' to path '{path}'.");
            string command;
            if (string.IsNullOrWhiteSpace(fsrmServerName))
            {
                command = $"Import-Module FileServerResourceManager; New-FsrmQuota -Path '{path}' -Template '{quotaTemplateName}' -ErrorAction Stop";
            }
            else
            {
                command = $"Import-Module FileServerResourceManager; New-FsrmQuota -CimSession '{fsrmServerName}' -Path '{path}' -Template '{quotaTemplateName}' -ErrorAction Stop";
            }
            
            _logger.LogInformation($"Executing FSRM command: {command}");
            
            // Placeholder for actual command execution using a tool like run_terminal_cmd
            // For now, we'll just log. If IToolbelt was available, it would be used here.
            // Example: var result = await _toolbelt.RunTerminalCmd(command, false, "Applying FSRM quota.");
            // Check result for success/failure and log accordingly.
            
            _logger.LogWarning("ApplyFsrmQuotaAsync: PowerShell command execution is currently a placeholder. Implement with actual terminal execution tool.");
            await Task.CompletedTask; // Simulate async work
        }

        private async Task<bool> CreateFoldersFromTemplateAsync(string basePath, FolderTemplate template, object entityInfo)
        {
            try
            {
                if (!Directory.Exists(basePath))
                {
                    Directory.CreateDirectory(basePath);
                    _logger.LogInformation($"Created base directory: {basePath}");
                }

                if (template.SubPaths != null)
                {
                    foreach (var subPathPattern in template.SubPaths)
                    {
                        var subPath = await ResolvePathPlaceholdersAsync(subPathPattern, entityInfo);
                        var fullPath = Path.Combine(basePath, SanitizeFolderName(subPath));
                        if (!Directory.Exists(fullPath))
                        {
                            Directory.CreateDirectory(fullPath);
                            _logger.LogInformation($"Created sub-directory: {fullPath}");
                        }
                    }
                }
                return true;
            }
            catch (System.Exception ex)
            {
                // var folderSettings = await GetFolderSettingsAsync(); // Not needed here, template.Name is available
                _logger.LogError(ex, $"Error creating folders for path {basePath} using template {template?.Name ?? "unknown"}.");
                return false;
            }
        }

        private Task<string> ResolvePathPlaceholdersAsync(string pathPattern, object entityInfo)
        {
            var resolvedPath = Regex.Replace(pathPattern, @"\{(\w+)\}", match =>
            {
                var propertyName = match.Groups[1].Value;
                var property = entityInfo.GetType().GetProperty(propertyName);
                if (property != null)
                {
                    return SanitizeFolderName(property.GetValue(entityInfo)?.ToString() ?? string.Empty);
                }
                _logger.LogWarning($"Placeholder '{{{propertyName}}}' not found in entity type {entityInfo.GetType().Name}.");
                return match.Value;
            });
            return Task.FromResult(resolvedPath);
        }

        private string SanitizeFolderName(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return "_undefined_";
            }
            string invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            string regexSearch = string.Join("", invalidChars.Select(c => Regex.Escape(c.ToString())));
            Regex r = new Regex($"[{regexSearch}]");
            return r.Replace(folderName, "_").Replace("..", "_").TrimEnd('.');
        }
    }
} 