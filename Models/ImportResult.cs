using System;
using System.Collections.Generic;

namespace ADManagerAPI.Models
{
    public class ImportResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int TotalActions { get; set; }

        public int ProcessedCount { get; set; }

        public int SuccessCount { get; set; }

        public int ErrorCount { get; set; }

        public int CreateCount { get; set; }
        
        public int UpdateCount { get; set; }
        
        public int DeleteCount { get; set; }
        
        public int CreateOUCount { get; set; }
        
        public int DeleteOUCount { get; set; }

        public int MoveCount { get; set; }

        public int ProvisionShareCount { get; set; }
        
        public int CreateClassGroupFolderCount { get; set; }

        public int CreateTeamGroupCount { get; set; }

        public int CreatedTeams { get; set; }

        public List<string> Messages { get; set; } = new List<string>();
        
        public List<string> Warnings { get; set; } = new List<string>();

        public List<string> Errors { get; set; } = new List<string>();

        public List<ImportActionResult> Results { get; set; } = new List<ImportActionResult>();

        public int TotalProcessed => ProcessedCount;

        public int TotalSucceeded => SuccessCount;

        public int TotalFailed => ErrorCount;

        public List<ImportActionResult> Details
        {
            get => Results;
            set => Results = value;
        }

        public ImportSummary Summary
        {
            get
            {
                return new ImportSummary
                {
                    TotalObjects = TotalActions,
                    ProcessedCount = ProcessedCount,
                    CreateCount = CreateCount,
                    UpdateCount = UpdateCount,
                    DeleteCount = DeleteCount,
                    ErrorCount = ErrorCount,
                    CreateOUCount = CreateOUCount,
                    DeleteOUCount = DeleteOUCount,
                    MoveCount = MoveCount,
                    ProvisionUserShareCount = ProvisionShareCount,
                    CreateClassGroupFolderCount = CreateClassGroupFolderCount,
                    CreateTeamGroupCount = CreateTeamGroupCount
                };
            }
        }
    }
} 