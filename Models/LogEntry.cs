using System;
using System.Collections.Generic;

namespace ADManagerAPI.Models
{
    public class LogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        
        public LogLevel Level { get; set; } = LogLevel.Information;

        public string LevelText { get; set; } = "info";

        public string Message { get; set; } = string.Empty;
        
        public string? Category { get; set; }

        public string? Source { get; set; }
        
        public string? Details { get; set; }
        
        public string? Username { get; set; }

        public string? Type { get; set; }
        
        public string? Action { get; set; }
        
        public string? StackTrace { get; set; }

        public Dictionary<string, object>? Data { get; set; }
    }

    public enum LogLevel
    {
        Trace,
        Debug,
        Information,
        Warning,
        Error,
        Critical
    }
} 