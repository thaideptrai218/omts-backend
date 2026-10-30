
namespace OMTS.Application.Common.Models.Reports;

/// <summary>
/// One worksheet: a header row, then one row per item. Cell values are strings, numbers, booleans,
/// <see cref="DateTime"/> (already in business local time) or null for an empty cell.
/// </summary>
public sealed record SpreadsheetSheet(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows);
