using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace ADManagerAPI.Services.Parse;

public class CsvParserService : ISpreadsheetDataParser
{
    public bool CanHandle(string fileExtension)
    {
        return fileExtension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<Dictionary<string, string>>> ParseAsync(
        Stream fileStream,
        string fileName,
        char csvDelimiter = ';',
        List<string>? manualColumns = null,
        CancellationToken cancellation = default)
    {
        var encoding = DetectEncoding(fileStream);

        fileStream.Position = 0;

        using var reader = new StreamReader(fileStream, encoding);


        fileStream.Position = 0;
        reader.DiscardBufferedData();
        
        bool forceManualColumns = manualColumns != null && manualColumns.Any();
        
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = csvDelimiter.ToString(),
            BadDataFound = null,
            HasHeaderRecord = !forceManualColumns && (manualColumns == null || !manualColumns.Any())
        };
        
        if (forceManualColumns)
        {
            Console.WriteLine($"CORECTION: HasHeaderRecord = false car manualColumns fournies ({manualColumns?.Count} colonnes)");
        }

        using var csv = new CsvReader(reader, csvConfig);

        string[] headers;
        if (csvConfig.HasHeaderRecord && !forceManualColumns)
        {
            await csv.ReadAsync();
            csv.ReadHeader();
            headers = csv.HeaderRecord;
        }
        else
        {
            if (manualColumns == null || !manualColumns.Any()) return new List<Dictionary<string, string>>();
            headers = manualColumns.ToArray();
        }

        var rows = new List<Dictionary<string, string>>();
        var rowCount = 0;

        while (await csv.ReadAsync())
        {
            cancellation.ThrowIfCancellationRequested();

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
            {
                var value = csv.GetField(i) ?? string.Empty;
                dict[headers[i]] = value;
            }

            rows.Add(dict);
            rowCount++;
        }

        return rows;
    }

    /// <summary>
    ///     Détecte l'encodage du fichier CSV
    /// </summary>
    private Encoding DetectEncoding(Stream stream)
    {
        var buffer = new byte[4096];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        // Recherche du BOM (Byte Order Mark)
        if (bytesRead >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return new UTF8Encoding(true);

        if (bytesRead >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return Encoding.Unicode; // UTF-16 Little Endian

        if (bytesRead >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            return Encoding.BigEndianUnicode; // UTF-16 Big Endian

        // Test pour UTF-8 valide
        try
        {
            var content = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var bytes = Encoding.UTF8.GetBytes(content);
            if (CompareBytes(buffer, bytes, bytesRead)) return Encoding.UTF8;
        }
        catch
        {
            // UTF-8 invalide, essayer d'autres encodages
        }

        return Encoding.GetEncoding("Windows-1252");
    }

    /// <summary>
    ///     Compare deux tableaux de bytes
    /// </summary>
    private bool CompareBytes(byte[] a, byte[] b, int length)
    {
        if (b.Length < length) return false;
        for (var i = 0; i < length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }
}