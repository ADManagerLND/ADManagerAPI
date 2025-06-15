using ADManagerAPI.Services.Parse;
using OfficeOpenXml;

namespace ADManagerAPI.Services.Parse
{
    public class ExcelParserService : ISpreadsheetDataParser
    {
        
    public bool CanHandle(string fileExtension)
    {
        return fileExtension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
               fileExtension.Equals(".xls", StringComparison.OrdinalIgnoreCase);
    }
    
    public async Task<List<Dictionary<string,string>>> ParseAsync(
        Stream fileStream,
        string fileName,
        char csvDelimiter = ';',
        List<string>? manualColumns = null,
        CancellationToken cancellation = default)
    {
        // EPPlus fonctionne de façon synchrone
        ExcelPackage.License.SetNonCommercialOrganization("My Noncommercial organization"); 
        using var package = new ExcelPackage(fileStream);
        var sheet = package.Workbook.Worksheets.First();

        int colCount = sheet.Dimension.End.Column;
        int rowCount = sheet.Dimension.End.Row;

        // 1) Lire les en-têtes
        var headers = new List<string>();
        if (manualColumns != null && manualColumns.Any())
        {
            headers.AddRange(manualColumns);
        }
        else
        {
            for (int c = 1; c <= colCount; c++)
                headers.Add(sheet.Cells[1, c].Text.Trim());
        }
        
        var rows = new List<Dictionary<string,string>>(rowCount - 1);

        // 2) Pour chaque ligne de données, construire le dictionnaire
        for (int r = 2; r <= rowCount; r++)
        {
            cancellation.ThrowIfCancellationRequested();

            var dict = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 1; c <= headers.Count; c++)
            {
                if (c <= sheet.Dimension.End.Column)
                {
                    dict[headers[c - 1]] = sheet.Cells[r, c].Text;
                }
                else
                {
                    dict[headers[c - 1]] = string.Empty; // Ou gérer l'absence de cellule autrement
                }
            }
            rows.Add(dict);
        }

        return rows;
        }
    }
}