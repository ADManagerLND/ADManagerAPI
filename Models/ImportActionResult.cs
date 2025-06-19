using System.Text.Json.Serialization;

namespace ADManagerAPI.Models;

/// <summary>
///     Résultat d'une action d'import individuelle avec informations temporelles
/// </summary>
public class ImportOperationResult
{
    /// <summary>
    ///     Indique si l'action a réussi
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Type d'action réalisée
    /// </summary>
    public ActionType ActionType { get; set; }

    /// <summary>
    ///     Nom de l'objet traité
    /// </summary>
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>
    ///     Chemin de l'objet traité
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Message de résultat de l'action
    /// </summary>
    public string Message { get; set; } = string.Empty;


    /// <summary>
    ///     Message d'erreur simplifié pour la sérialisation JSON
    /// </summary>
    public string? ErrorMessage
    {
        get => Exception?.Message;
        set
        {
            /* Lecture seule, calculé depuis Exception */
        }
    }

    /// <summary>
    ///     Type d'exception pour la sérialisation JSON
    /// </summary>
    public string? ErrorType
    {
        get => Exception?.GetType().Name;
        set
        {
            /* Lecture seule, calculé depuis Exception */
        }
    }


    // ✅ Propriétés manquantes pour le suivi temporel et les erreurs
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime EndTime { get; set; } = DateTime.Now;
    public TimeSpan Duration { get; set; }
    
    [JsonIgnore]
    public Exception? Exception { get; set; }
}

/// <summary>
///     Alias pour ImportOperationResult pour la compatibilité
/// </summary>
public class ImportActionResult : ImportOperationResult
{
}