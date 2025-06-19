using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces;

public interface ISignalRService
{
    Task SendImportStartedAsync(string? connectionId, int totalActions);

    Task SendImportProgressAsync(string? connectionId, int current, int total, string message);

    Task SendImportCompletedAsync(string? connectionId, ImportResult result);

    Task SendCsvAnalysisErrorAsync(string connectionId, string errorMessage);

    Task SendCsvAnalysisProgressAsync(string connectionId, int progress, string status, string message,
        ImportAnalysis? analysis = null);

    Task SendCsvAnalysisCompleteAsync(string connectionId, AnalysisResult result);

    Task SendCsvImportCompleteAsync(string connectionId, ImportResult result);

    Task ProcessCsvUpload(string? connectionId, Stream fileStream, string fileName, ImportConfig config);
    Task<bool> IsConnectedAsync();
}