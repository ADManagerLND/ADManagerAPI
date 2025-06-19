using OfficeOpenXml;

namespace ADManagerAPI.Services.Parse;

public class ExcelParserService : ISpreadsheetDataParser
{
    public bool CanHandle(string fileExtension)
    {
        return fileExtension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
               fileExtension.Equals(".xls", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<Dictionary<string, string>>> ParseAsync(
        Stream fileStream,
        string fileName,
        char csvDelimiter = ';',
        List<string>? manualColumns = null,
        CancellationToken cancellation = default)
    {
        ExcelPackage.License.SetNonCommercialOrganization("My Noncommercial organization");
        using var package = new ExcelPackage(fileStream);
        var sheet = package.Workbook.Worksheets.First();

        var colCount = sheet.Dimension?.End.Column ?? 0;
        var rowCount = sheet.Dimension?.End.Row ?? 0;
        if (rowCount == 0) return new List<Dictionary<string, string>>();

        var headers = new List<string>();
        int startRow;

        var useManualHeaders = manualColumns != null && manualColumns.Any();

        if (useManualHeaders)
        {
            headers.AddRange(manualColumns!);
            startRow = 1;
        }
        else
        {
            for (var c = 1; c <= colCount; c++)
            {
                headers.Add(sheet.Cells[1, c].Text.Trim());
            }
            startRow = 2;
        }
        
        var rows = new List<Dictionary<string, string>>(rowCount - startRow + 1);
        
        for (var r = startRow; r <= rowCount; r++)
        {
            cancellation.ThrowIfCancellationRequested();

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var isRowEmpty = true;
            for (var c = 1; c <= headers.Count; c++)
            {
                var cellValue = c <= colCount ? sheet.Cells[r, c].Text : string.Empty;
                dict[headers[c - 1]] = cellValue;
                if (!string.IsNullOrWhiteSpace(cellValue))
                {
                    isRowEmpty = false;
                }
            }
            
            if (!isRowEmpty)
            {
                rows.Add(dict);
            }
        }

        return rows;
    }
}