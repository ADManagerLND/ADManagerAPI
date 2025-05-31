using System;
using System.Collections.Generic;

namespace ADManagerAPI.Models
{
    public class ImportResult
    {
        public bool Success { get; set; } = true;
        public int TotalProcessed { get; set; }
        public int TotalSucceeded { get; set; }
        public int TotalFailed { get; set; }
        public string Details { get; set; } = "";
        public ImportSummary Summary { get; set; } = new ImportSummary();
        public List<ImportActionResult> ActionResults { get; set; } = [];
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int DeletedCount { get; set; }
        public int MovedCount { get; set; }
        public int ErrorCount { get; set; }
    }

    public class ImportError
    {
        public string Field { get; set; } = "";
        public string Message { get; set; } = "";
        public string Value { get; set; } = "";
    }
} 