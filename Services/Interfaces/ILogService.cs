using ADManagerAPI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ADManagerAPI.Services.Interfaces;

/// <summary>
///     Interface pour les services de journalisation
/// </summary>
public interface ILogService
{
    /// <summary>
    ///     Enregistre une action utilisateur
    /// </summary>
    void LogUserAction(string username, string action, string details);

    /// <summary>
    ///     Enregistre une erreur système
    /// </summary>
    void LogError(string source, string message, Exception? exception = null);

    /// <summary>
    ///     Récupère les entrées de journal les plus récentes
    /// </summary>
    List<LogEntry> GetRecentLogEntries(int count = 100);

    /// <summary>
    ///     Recherche dans les entrées de journal
    /// </summary>
    List<LogEntry> SearchLogEntries(string searchTerm, DateTime? startDate = null, DateTime? endDate = null);

    void Log(string category, string message);

    Task<List<LogEntry>> GetLogs(int count = 100);
}