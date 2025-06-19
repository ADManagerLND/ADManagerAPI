using System.Text.Json.Serialization;

namespace ADManagerAPI.Models;

public class FsrmSettings
{
    [JsonPropertyName("enableFsrmQuotas")] public bool EnableFsrmQuotas { get; set; } = false;

    [JsonPropertyName("fsrmServerName")] public string? FsrmServerName { get; set; }

    [JsonPropertyName("quotaTemplatesByRole")]
    public Dictionary<UserRole, string> QuotaTemplatesByRole { get; set; } = new();

    [JsonPropertyName("defaultQuotaTemplate")]
    public string DefaultQuotaTemplate { get; set; } = "1GB_Limit";

    [JsonPropertyName("enableFileScreening")]
    public bool EnableFileScreening { get; set; } = false;

    [JsonPropertyName("fileScreenTemplatesByRole")]
    public Dictionary<UserRole, string> FileScreenTemplatesByRole { get; set; } = new();

    [JsonPropertyName("defaultFileScreenTemplate")]
    public string DefaultFileScreenTemplate { get; set; } = "Block_Executables";

    [JsonPropertyName("enableReporting")] public bool EnableReporting { get; set; } = true;

    [JsonPropertyName("reportSchedule")] public string ReportSchedule { get; set; } = "Weekly";
}