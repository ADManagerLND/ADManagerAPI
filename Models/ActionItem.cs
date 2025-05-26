namespace ADManagerAPI.Models
{
    public class LegacyImportActionItem
    {
        public int RowIndex { get; set; }
        public string ActionType { get; set; }
        public Dictionary<string, string> Data { get; set; } = new();
        public bool IsValid { get; set; } = true;
        public List<string> ValidationErrors { get; set; } = [];
    }
} 