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
public sealed record SpreadsheetSheet(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows);
