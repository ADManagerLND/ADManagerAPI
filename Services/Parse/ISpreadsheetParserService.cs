namespace ADManagerAPI.Services.Parse;

public interface ISpreadsheetDataParser
{

    bool CanHandle(string fileExtension);
    
    Task<List<Dictionary<string,string>>> ParseAsync(
        Stream fileStream,
        string fileName,
        char csvDelimiter = ';',
        List<string>? manualColumns = null,
        CancellationToken cancellation = default);
}
