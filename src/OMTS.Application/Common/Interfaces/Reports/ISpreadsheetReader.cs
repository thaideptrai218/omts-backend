namespace OMTS.Application.Common.Interfaces.Reports;

/// <summary>Reads the first worksheet of an uploaded .xlsx file, the counterpart of <see cref="ISpreadsheetWriter"/>.</summary>
public interface ISpreadsheetReader
{
    /// <summary>The sheet, or null when the content is not a readable workbook or unpacks to more than the safe size.</summary>
    SpreadsheetContent? Read(Stream content, int maxRows);
}

/// <param name="Headers">Trimmed header texts of row 1.</param>
/// <param name="Rows">Non-empty data rows.</param>
/// <param name="TooManyRows">More than the allowed rows are present; <paramref name="Rows"/> is then empty.</param>
public sealed record SpreadsheetContent(IReadOnlyList<string> Headers, IReadOnlyList<SpreadsheetRow> Rows, bool TooManyRows);

/// <param name="Number">The row number in the sheet, as the user sees it.</param>
/// <param name="Cells">Trimmed text by column index, same order as the headers; empty cells are null.</param>
/// <param name="HasFormula">A cell holds a formula: never evaluated, the row is refused.</param>
public sealed record SpreadsheetRow(int Number, IReadOnlyList<string?> Cells, bool HasFormula);
