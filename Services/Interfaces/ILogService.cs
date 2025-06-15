using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces;

public interface ILogService
{
    
    void LogUserAction(string username, string action, string details);
    void LogError(string source, string message, Exception? exception = null);
    List<LogEntry> GetRecentLogEntries(int count = 100);
    List<LogEntry> SearchLogEntries(string searchTerm, DateTime? startDate = null, DateTime? endDate = null);
    void Log(string category, string message);
    Task<List<LogEntry>> GetLogs(int count = 100);
    Task LogActionAsync(LogAction action, string objectName, string details);
}