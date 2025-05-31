using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces
{
    public interface ISpreadsheetImportService // Renamed from ICsvManagerService
    {
        #region Méthodes d'analyse de tableur (anciennement ICsvAnalysisService)
        
        Task<AnalysisResult> AnalyzeSpreadsheetContentAsync(Stream fileStream, string fileName, ImportConfig config);
        
        Task<AnalysisResult> AnalyzeSpreadsheetDataAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config);
        
        Task<ImportResult> ExecuteImportFromActionsAsync(List<Dictionary<string, string>> spreadsheetData, ImportConfig config, List<LegacyImportActionItem> actions, string? connectionId = null);
        
        #endregion

        /// <summary>
        /// Exécute un import à partir d'une analyse préalablement effectuée.
        /// </summary>
        Task<ImportResult> ExecuteImportFromAnalysisAsync(ImportAnalysis analysis, ImportConfig config, string? connectionId = null);

        Task<ImportResult> ProcessSpreadsheetDataAsync(List<Dictionary<string, string>> data, ImportConfig config);
    }
} 