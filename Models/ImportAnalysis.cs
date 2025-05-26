namespace ADManagerAPI.Models
{
    public class ImportAnalysis
    {
        public List<ImportAction> Actions { get; set; } = new List<ImportAction>();
        public ImportSummary Summary { get; set; } = new ImportSummary();
        public List<Dictionary<string, string>> CsvData { get; set; } = new List<Dictionary<string, string>>();
    }

    public class ImportSummary
    {
        public int TotalObjects { get; set; }
        public int CreateCount { get; set; }
        public int CreateOUCount { get; set; }
        public int UpdateCount { get; set; }
        public int DeleteOUCount { get; set; }
        public int DeleteCount { get; set; }
        public int MoveCount { get; set; }
        public int ErrorCount { get; set; }
        public int ProcessedCount { get; set; }
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