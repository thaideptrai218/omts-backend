using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;
using OMTS.Application.Common.Interfaces.Reports;

namespace OMTS.Infrastructure.Reports;

/// <summary>Reads uploaded workbooks with ClosedXML. The unpacked size is checked first: a small file can expand enormously.</summary>
internal sealed class ClosedXmlSpreadsheetReader : ISpreadsheetReader
{
    private const long MaxUnpackedBytes = 50L * 1024 * 1024;

    public SpreadsheetContent? Read(Stream content, int maxRows)
    {
        try
        {
            using (var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (archive.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
                {
                    return null;
                }
            }

            content.Position = 0;
            using var workbook = new XLWorkbook(content);
            var sheet = workbook.Worksheets.First();
            var used = sheet.RangeUsed();
            if (used is null)
            {
                return new SpreadsheetContent([], [], false);
            }

            var lastColumn = used.LastColumn().ColumnNumber();
            var lastRow = used.LastRow().RowNumber();
            var headers = Enumerable.Range(1, lastColumn).Select(c => Text(sheet.Cell(1, c)) ?? string.Empty).ToList();
            if (lastRow - 1 > maxRows)
            {
                return new SpreadsheetContent(headers, [], true);
            }

            var rows = new List<SpreadsheetRow>();
            for (var number = 2; number <= lastRow; number++)
            {
                var cells = Enumerable.Range(1, lastColumn).Select(c => sheet.Cell(number, c)).ToList();
                var values = cells.Select(Text).ToList();
                if (values.Any(v => v is not null) || cells.Any(c => c.HasFormula))
                {
                    rows.Add(new SpreadsheetRow(number, values, cells.Any(c => c.HasFormula)));
                }
            }

            return new SpreadsheetContent(headers, rows, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Whatever is not a workbook ClosedXML can open.
            return null;
        }
    }

    private static string? Text(IXLCell cell)
    {
        if (cell.HasFormula)
        {
            return null;
        }

        var value = cell.Value;
        // A date cell is read as the date it holds, whatever display format the sheet gives it ("m/d/yyyy" or "dd/mm/yyyy" cannot be told apart as text).
        var text = value.IsNumber ? value.GetNumber().ToString("0.##########", CultureInfo.InvariantCulture)
            : value.IsDateTime ? value.GetDateTime().ToString(value.GetDateTime().TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value.IsBlank ? null : cell.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
