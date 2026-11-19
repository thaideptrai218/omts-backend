namespace OMTS.Application.Common.Interfaces.Reports;

/// <summary>Writes report data to an Excel workbook (.xlsx) for the export flows of the reporting screens.</summary>
public interface ISpreadsheetWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The workbook with one worksheet per sheet, in order; throws when the file cannot be generated.</summary>
    byte[] Write(IReadOnlyList<SpreadsheetSheet> sheets);
}

/// <summary>
/// One worksheet: a header row, then one row per item. Cell values are strings, numbers, booleans,
/// <see cref="DateTime"/> (already in business local time) or null for an empty cell.
/// </summary>
/// <remarks><c>QrCodeText</c>: when set, a QR code of that text is drawn to the right of the table with <c>QrCodeCaption</c> above it (the provenance QR of a printed invoice).</remarks>
public sealed record SpreadsheetSheet(
    string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows, string? QrCodeText = null, string? QrCodeCaption = null);
