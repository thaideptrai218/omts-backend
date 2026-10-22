using ClosedXML.Excel;
using OMTS.Application.Common.Interfaces.Reports;

namespace OMTS.Infrastructure.Reports;

/// <summary>.xlsx export with ClosedXML: a bold, frozen header row and one row per item. Text is never read as a formula.</summary>
internal sealed class ClosedXmlSpreadsheetWriter : ISpreadsheetWriter
{
    private const string DateTimeFormat = "dd/MM/yyyy HH:mm";
    private const double MaxColumnWidth = 60;

    public byte[] Write(IReadOnlyList<SpreadsheetSheet> sheets)
    {
        using var workbook = new XLWorkbook();
        foreach (var sheet in sheets)
        {
            var worksheet = workbook.Worksheets.Add(sheet.Name);
            for (var column = 0; column < sheet.Headers.Count; column++)
            {
                var header = worksheet.Cell(1, column + 1);
                header.Value = sheet.Headers[column];
                header.Style.Font.Bold = true;
                // Measured without fonts (AdjustToContents needs them): about one character per unit.
                worksheet.Column(column + 1).Width = Math.Min(MaxColumnWidth, Math.Max(12, sheet.Headers[column].Length + 2));
            }

            for (var row = 0; row < sheet.Rows.Count; row++)
            {
                var values = sheet.Rows[row];
                for (var column = 0; column < values.Count; column++)
                {
                    SetValue(worksheet.Cell(row + 2, column + 1), values[column]);
                }
            }

            worksheet.SheetView.FreezeRows(1);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void SetValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case DateTime dateTime:
                cell.Value = dateTime;
                cell.Style.DateFormat.Format = DateTimeFormat;
                break;
            case decimal number:
                cell.Value = number;
                break;
            case int number:
                cell.Value = number;
                break;
            case double number:
                cell.Value = number;
                break;
            case bool flag:
                cell.Value = flag;
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }
}
