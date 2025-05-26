using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LogLevel = ADManagerAPI.Models.LogLevel;

namespace ADManagerAPI.Services;

public class LogService : ILogService
{
    private static readonly object _lockObject = new();
    private readonly string _logFilePath;
    private readonly ILogger<LogService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly List<LogEntry> _logEntries = new();

    public LogService(ILogger<LogService> logger, IServiceScopeFactory serviceScopeFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "admanager_logs.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath));
    }
    
    public void LogUserAction(string username, string action, string details)
    {
        var logEntry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Type = "UserAction",
            Username = username,
            Action = action,
            Details = details
        };

        SaveLogEntry(logEntry);
    }
    
    public void LogError(string source, string message, Exception? exception = null)
    {
        var details = exception != null
            ? $"{message} - Exception: {exception.Message}"
            : message;

        var logEntry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Type = "Error",
            Source = source,
            Details = details,
            StackTrace = exception?.StackTrace
        };

        SaveLogEntry(logEntry);
    }
    
    public List<LogEntry> GetRecentLogEntries(int count = 100)
    {
        return GetAllLogEntries().OrderByDescending(l => l.Timestamp).Take(count).ToList();
    }
    
    public List<LogEntry> SearchLogEntries(string searchTerm, DateTime? startDate = null, DateTime? endDate = null)
    {
        var logs = GetAllLogEntries();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            searchTerm = searchTerm.ToLower();
            logs = logs.Where(l =>
                (l.Username?.ToLower().Contains(searchTerm) ?? false) ||
                (l.Action?.ToLower().Contains(searchTerm) ?? false) ||
                (l.Details?.ToLower().Contains(searchTerm) ?? false) ||
                (l.Source?.ToLower().Contains(searchTerm) ?? false)
            ).ToList();
        }

        if (startDate.HasValue) logs = logs.Where(l => l.Timestamp >= startDate.Value).ToList();

        if (endDate.HasValue) logs = logs.Where(l => l.Timestamp <= endDate.Value).ToList();

        return logs.OrderByDescending(l => l.Timestamp).ToList();
    }

    public void Log(string category, string message)
    {
        var logEntry = new LogEntry
        {
            Category = category,
            Message = message,
            Timestamp = DateTime.Now,
            Level = LogLevel.Information,
            LevelText = "info"
        };
        
        _logEntries.Add(logEntry);
        _logger.LogInformation($"[{category}] {message}");
    }

    public Task<List<LogEntry>> GetLogs(int count = 100)
    {
        return Task.FromResult(_logEntries.OrderByDescending(l => l.Timestamp).Take(count).ToList());
    }

    private void SaveLogEntry(LogEntry logEntry)
    {
        lock (_lockObject)
        {
            try
            {
                var logLine =
                    $"{logEntry.Timestamp:yyyy-MM-dd HH:mm:ss}|{logEntry.Type}|{logEntry.Username ?? ""}|{logEntry.Source ?? ""}|{logEntry.Action ?? ""}|{logEntry.Details}";

                if (!string.IsNullOrEmpty(logEntry.StackTrace))
                    logLine += $"|{logEntry.StackTrace.Replace(Environment.NewLine, " ")}";

                File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // En cas d'erreur, ne pas planter l'application mais loguer dans la console
                Console.WriteLine($"Erreur lors de l'enregistrement du log: {ex.Message}");
            }
        }
    }

    private List<LogEntry> GetAllLogEntries()
    {
        var logs = new List<LogEntry>();

        lock (_lockObject)
        {
            try
            {
                if (File.Exists(_logFilePath))
                {
                    var lines = File.ReadAllLines(_logFilePath);

                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 5)
                        {
                            var logEntry = new LogEntry
                            {
                                Timestamp = DateTime.Parse(parts[0]),
                                Type = parts[1],
                                Username = parts[2],
                                Source = parts[3],
                                Action = parts[4],
                                Details = parts.Length > 5 ? parts[5] : "",
                                StackTrace = parts.Length > 6 ? parts[6] : null
                            };

                            logs.Add(logEntry);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur lors de la lecture des logs: {ex.Message}");
            }
        }

        return logs;
    }

    public List<LogModel> GetAllLogs()
    {
        lock (_lockObject)
        {
            if (!File.Exists(_logFilePath))
                return new List<LogModel>();

            var logs = new List<LogModel>();
            var lines = File.ReadAllLines(_logFilePath);

            foreach (var line in lines)
                try
                {
                    // Parse format: "2023-04-10 15:30:45 [ACTION] Message"
                    var timestampEndPos = line.IndexOf(" [");
                    var actionStartPos = timestampEndPos + 2; // Skip space and opening bracket
                    var actionEndPos = line.IndexOf("] ", actionStartPos);
                    var messageStartPos = actionEndPos + 2; // Skip closing bracket and space

                    if (timestampEndPos > 0 && actionEndPos > 0)
                    {
                        var timestamp = DateTime.Parse(line.Substring(0, timestampEndPos));
                        var action = line.Substring(actionStartPos, actionEndPos - actionStartPos);
                        var message = line.Substring(messageStartPos);

                        logs.Add(new LogModel
                        {
                            Timestamp = timestamp,
                            Action = action,
                            Message = message
                        });
                    }
                }
                catch
                {
                    // Skip malformed log entries
                }

            return logs;
        }
    }

    public void ClearLogs()
    {
        lock (_lockObject)
        {
            if (File.Exists(_logFilePath))
                File.WriteAllText(_logFilePath, string.Empty);
        }
    }
}