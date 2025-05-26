using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces
{
    public interface ICsvManagerService
    {
        #region Méthodes d'analyse CSV (anciennement ICsvAnalysisService)
        
        Task<CsvAnalysisResult> AnalyzeCsvContentAsync(Stream fileStream, string fileName, ImportConfig config);
        
        Task<CsvAnalysisResult> AnalyzeCsvDataAsync(List<Dictionary<string, string>> csvData, ImportConfig config);
        
        Task<ImportResult> ExecuteImportFromActionsAsync(List<Dictionary<string, string>> csvData, ImportConfig config, List<LegacyImportActionItem> actions, string? connectionId = null);
        
        #endregion

        /// <summary>
        /// Exécute un import depuis les données CSV sans passer par des actions prédéfinies.
        /// Cette méthode analyse les données et génère automatiquement les actions appropriées.
        /// </summary>
        Task<ImportResult> ExecuteImportFromDataAsync(List<Dictionary<string, string>> csvData, ImportConfig config, string? connectionId = null);

        Task<ImportResult> ProcessCsvDataAsync(List<Dictionary<string, string>> data, ImportConfig config);
    }
} 