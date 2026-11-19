namespace OMTS.Application.Features.Reports.Documents;

/// <summary>How a cell is aligned in its column.</summary>
public enum ReportAlign
{
    Left,
    Center,
    Right,
}

/// <summary>How a cell value is written: text as is, a whole number (#,##0) or a quantity in kg (#,##0.00).</summary>
public enum ReportCellFormat
{
    Text,
    Integer,
    Kilogram,
}

/// <summary>
/// A column of a table. <c>Width</c> is a size in characters: Excel uses it as the column width, the PDF as the share of the page width.
/// <c>IsCode</c> marks a column of document codes (shown in blue on detail rows, like the links of KiotViet).
/// </summary>
public sealed record ReportColumn(string Title, double Width, ReportAlign Align = ReportAlign.Left, ReportCellFormat Format = ReportCellFormat.Text, bool IsCode = false);

/// <summary>Group: bold on beige with the totals of its rows. SubGroup: bold on grey, inside a group. Detail: one record.</summary>
public enum ReportRowKind
{
    Group,
    SubGroup,
    Detail,
}

/// <summary>
/// One row. <c>Cells</c> follow the columns: text, numbers (<see cref="decimal"/>, <see cref="int"/>) or null for an empty cell. On a group or sub-group row the label in the
/// first cell runs across the empty text cells that follow it.
/// </summary>
public sealed record ReportRow(ReportRowKind Kind, IReadOnlyList<object?> Cells)
{
    public static ReportRow Group(params object?[] cells) => new(ReportRowKind.Group, cells);

    public static ReportRow SubGroup(params object?[] cells) => new(ReportRowKind.SubGroup, cells);

    public static ReportRow Detail(params object?[] cells) => new(ReportRowKind.Detail, cells);
}

/// <summary>A table with an optional heading (the Excel sheet name too). The header row repeats on every PDF page and every printed Excel page.</summary>
public sealed record ReportSection(string? Heading, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<ReportRow> Rows);

/// <summary>
/// A printable report in the KiotViet style, independent of the file format: "Ngày lập" top left, the centred title and filter lines, the tables, the footer text centred with
/// "Trang: n/N" on the right. <c>CreatedAt</c> is in business local time. Rendered to PDF or Excel by an <c>IReportDocumentRenderer</c>.
/// </summary>
public sealed record ReportDocument(string Title, DateTime CreatedAt, IReadOnlyList<string> FilterLines, IReadOnlyList<ReportSection> Sections, string FooterText);
