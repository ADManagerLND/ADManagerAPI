namespace ADManagerAPI.Services.Parse;

public interface ISpreadsheetParserService
{
    /// <summary>
    /// Lit un flux CSV ou Excel et renvoie une liste de lignes,
    /// chaque ligne étant un dictionnaire "nomDeColonne → valeur".
    /// </summary>
    Task<List<Dictionary<string,string>>> ParseAsync(
        Stream fileStream,
        string fileName,
        char csvDelimiter = ';',
        List<string>? manualColumns = null,
        CancellationToken cancellation = default);
}
