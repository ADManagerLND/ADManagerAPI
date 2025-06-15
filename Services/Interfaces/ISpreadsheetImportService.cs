using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces
{
    public interface ISpreadsheetImportService
    {

        Task<AnalysisResult> AnalyzeSpreadsheetContentAsync(Stream fileStream, string fileName, ImportConfig config, string? connectionId = null, CancellationToken cancellationToken = default);
        
        Task<AnalysisResult> AnalyzeSpreadsheetDataAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, string? connectionId = null, CancellationToken cancellationToken = default);
        
        Task<ImportResult> ExecuteImportFromActionsAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, List<LegacyImportActionItem> actions, string? connectionId = null);

        Task<ImportResult> ExecuteImportFromAnalysisAsync(ImportAnalysis analysis, ImportConfig config, string? connectionId = null);

        Task<ImportResult> ProcessSpreadsheetDataAsync(List<Dictionary<string, string>> data, ImportConfig config);
    }
} 