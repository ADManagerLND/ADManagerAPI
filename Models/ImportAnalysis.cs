namespace ADManagerAPI.Models
{
    public class ImportAnalysis
    {
        public List<ImportAction> Actions { get; set; } = new List<ImportAction>();
        public ImportSummary Summary { get; set; } = new ImportSummary();
    }

    public class ImportAction
    {
        public ActionType ActionType { get; set; }
        public string ObjectName { get; set; }
        public string Path { get; set; }
        public string Message { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
        public int RowIndex { get; set; } = 0;
    }
    
} 