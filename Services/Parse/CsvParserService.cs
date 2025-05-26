using System.Globalization;
using System.Text;
using ADManagerAPI.Services.Parse;
using CsvHelper;
using CsvHelper.Configuration;

public class CsvParserService : ISpreadsheetParserService
{
    public async Task<List<Dictionary<string,string>>> ParseAsync(
        Stream fileStream,
        string fileName,
        char csvDelimiter = ';',
        List<string>? manualColumns = null,
        CancellationToken cancellation = default)
    {
        using var reader = new StreamReader(fileStream, Encoding.UTF8);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = csvDelimiter.ToString(),
            BadDataFound = null,
            HasHeaderRecord = (manualColumns == null || !manualColumns.Any())
        };

        using var csv    = new CsvReader(reader, csvConfig);

        string[] headers;
        if (csvConfig.HasHeaderRecord)
        {
            await csv.ReadAsync();
            csv.ReadHeader();
            headers = csv.HeaderRecord;
        }
        else
        {
            if (manualColumns == null || !manualColumns.Any())
            {
                return new List<Dictionary<string, string>>();
            }
            headers = manualColumns.ToArray();
        }

        var rows = new List<Dictionary<string,string>>();
        while (await csv.ReadAsync())
        {
            cancellation.ThrowIfCancellationRequested();

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                dict[headers[i]] = csv.GetField(i) ?? string.Empty;
            }
            rows.Add(dict);
        }

        return rows;
    }
}