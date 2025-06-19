namespace ADManagerAPI.Models;

public class LegacyImportActionItem
{
    public int RowIndex { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = new();
    public bool IsValid { get; set; } = true;
    public List<string> ValidationErrors { get; set; } = [];
    public bool Selected { get; set; } = true;
    public string ObjectName => Data.GetValueOrDefault("objectName", "");
    public string OuPath => Data.GetValueOrDefault("path", "");
    public string Message => Data.GetValueOrDefault("message", "");
}