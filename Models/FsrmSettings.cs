using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ADManagerAPI.Models
{
    public class FsrmSettings
    {
        [JsonPropertyName("enableFsrmQuotas")]
        public bool EnableFsrmQuotas { get; set; } = false;

        [JsonPropertyName("fsrmServerName")]
        public string? FsrmServerName { get; set; } // Optional: for remote FSRM server, null or empty for local

        [JsonPropertyName("quotaTemplatesByRole")]
        public Dictionary<UserRole, string> QuotaTemplatesByRole { get; set; } = new();
    }
} 