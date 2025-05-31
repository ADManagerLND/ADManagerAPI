using System.Collections.Generic;

namespace ADManagerAPI.Models
{
    public class AnalysisResult
    {
        public bool IsValid { get; set; } = true;
        public bool Success { get; set; } = true;
        public string? ErrorMessage { get; set; }
        public int TotalRows { get; set; }
        public List<string> Headers { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
        public List<string> Errors { get; set; } = [];
        public List<Dictionary<string, string>> Data { get; set; } = [];
        public List<Dictionary<string, string>>? CsvData { get; set; }
        public List<string>? CsvHeaders { get; set; }
        public ImportAnalysis? Analysis { get; set; }
        public List<object>? PreviewData { get; set; }
        public List<Dictionary<string, string>>? TableData { get; set; }
        public object? Summary { get; set; }
    }
} 