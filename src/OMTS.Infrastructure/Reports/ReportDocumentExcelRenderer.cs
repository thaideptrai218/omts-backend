using ClosedXML.Excel;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Features.Reports.Documents;

namespace OMTS.Infrastructure.Reports;

/// <summary>
/// A <see cref="ReportDocument"/> as an .xlsx in the KiotViet layout, one sheet per section: "Ngày lập" top left, the merged centred title and filter lines, the light blue header row (frozen,
/// repeated on every printed page), beige group rows, grey sub-groups and detail rows with thin grey borders. Numbers stay numbers (thousand separators by format). A4 portrait or landscape as the document says, one page wide.
/// </summary>
internal sealed class ReportDocumentExcelRenderer : IReportDocumentRenderer
{
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#B3E5FC");
    private static readonly XLColor GroupFill = XLColor.FromHtml("#F0EDD6");
    private static readonly XLColor SubGroupFill = XLColor.FromHtml("#F2F2F2");
    private static readonly XLColor Grey = XLColor.FromHtml("#BFBFBF");
    private static readonly XLColor CodeBlue = XLColor.FromHtml("#1565C0");

    public ReportFileFormat Format => ReportFileFormat.Xlsx;

    public string ContentType => ISpreadsheetWriter.ContentType;

    public string Extension => "xlsx";

    public byte[] Render(ReportDocument document)
    {
        using var workbook = new XLWorkbook();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in document.Sections)
        {
            WriteSheet(workbook.Worksheets.Add(SheetName(section.Heading, names)), document, section);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteSheet(IXLWorksheet sheet, ReportDocument document, ReportSection section)
    {
        var columns = section.Columns;
        var last = columns.Count;
        for (var c = 0; c < last; c++)
        {
            sheet.Column(c + 1).Width = columns[c].Width;
        }

        sheet.Style.Font.FontName = "Arial";
        sheet.Style.Font.FontSize = 10;
        var row = 1;
        Merged(sheet, row, last, $"Ngày lập: {document.CreatedAt:dd/MM/yyyy HH:mm}", XLAlignmentHorizontalValues.Left, bold: false);
        Merged(sheet, ++row, last, document.Title, XLAlignmentHorizontalValues.Center, bold: true).Style.Font.FontSize = 16;
        foreach (var line in document.FilterLines)
        {
            Merged(sheet, ++row, last, line, XLAlignmentHorizontalValues.Center, bold: false);
        }

        row += 2;
        if (section.Heading is not null)
        {
            Merged(sheet, row++, last, section.Heading, XLAlignmentHorizontalValues.Left, bold: true).Style.Font.FontSize = 12;
        }

        var headerRow = row;
        for (var c = 0; c < last; c++)
        {
            var cell = sheet.Cell(headerRow, c + 1);
            cell.Value = columns[c].Title;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = HeaderFill;
            cell.Style.Alignment.Horizontal = Horizontal(columns[c].Align == ReportAlign.Left ? ReportAlign.Left : ReportAlign.Center);
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Alignment.WrapText = true;
        }

        sheet.Row(headerRow).Height = 24;
        foreach (var item in section.Rows)
        {
            WriteRow(sheet, ++row, item, columns);
        }

        if (!string.IsNullOrWhiteSpace(section.Note))
        {
            var note = Merged(sheet, row += 2, last, section.Note, XLAlignmentHorizontalValues.Left, bold: false);
            note.Style.Font.Italic = true;
            note.Style.Font.FontSize = 9;
        }

        sheet.SheetView.FreezeRows(headerRow);
        var page = sheet.PageSetup;
        page.PageOrientation = document.Orientation == ReportOrientation.Landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        page.PaperSize = XLPaperSize.A4Paper;
        page.FitToPages(1, 0);
        page.SetRowsToRepeatAtTop(headerRow, headerRow);
        page.Margins.Left = page.Margins.Right = 0.4;
        page.Footer.Center.AddText(document.FooterText);
        var pageNumber = page.Footer.Right;
        pageNumber.AddText("Trang:");
        pageNumber.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
        pageNumber.AddText("/");
        pageNumber.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);
    }

    private static void WriteRow(IXLWorksheet sheet, int rowNumber, ReportRow item, IReadOnlyList<ReportColumn> columns)
    {
        var isDetail = item.Kind == ReportRowKind.Detail;
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = sheet.Cell(rowNumber, c + 1);
            var value = c < item.Cells.Count ? item.Cells[c] : null;
            var column = columns[c];
            switch (value)
            {
                case null:
                    break;
                case decimal number:
                    cell.Value = number;
                    break;
                case int number:
                    cell.Value = number;
                    break;
                default:
                    cell.Value = value.ToString();
                    break;
            }

            cell.Style.NumberFormat.Format = column.Format switch
            {
                ReportCellFormat.Integer => "#,##0",
                ReportCellFormat.Kilogram => "#,##0.00",
                _ => "General",
            };
            cell.Style.Alignment.Horizontal = Horizontal(column.Align);
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            if (isDetail)
            {
                cell.Style.Alignment.WrapText = true;
                cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.BottomBorderColor = Grey;
                if (column.IsCode)
                {
                    cell.Style.Font.FontColor = CodeBlue;
                }
            }
            else
            {
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = item.Kind == ReportRowKind.Group ? GroupFill : SubGroupFill;
            }
        }

        if (!isDetail)
        {
            sheet.Row(rowNumber).Height = 22;
        }
    }

    private static IXLCell Merged(IXLWorksheet sheet, int row, int lastColumn, string text, XLAlignmentHorizontalValues align, bool bold)
    {
        var range = sheet.Range(row, 1, row, lastColumn);
        if (lastColumn > 1)
        {
            range.Merge();
        }

        var cell = sheet.Cell(row, 1);
        cell.Value = text;
        cell.Style.Font.Bold = bold;
        cell.Style.Alignment.Horizontal = align;
        return cell;
    }

    private static XLAlignmentHorizontalValues Horizontal(ReportAlign align) => align switch
    {
        ReportAlign.Right => XLAlignmentHorizontalValues.Right,
        ReportAlign.Center => XLAlignmentHorizontalValues.Center,
        _ => XLAlignmentHorizontalValues.Left,
    };

    /// <summary>An Excel sheet name: at most 31 characters, none of []:*?/\, and unique in the workbook.</summary>
    private static string SheetName(string? heading, HashSet<string> used)
    {
        var name = new string((heading ?? "Báo cáo").Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray()).Trim();
        name = name.Length == 0 ? "Báo cáo" : name[..Math.Min(name.Length, 31)];
        var unique = name;
        for (var n = 2; !used.Add(unique); n++)
        {
            unique = $"{name[..Math.Min(name.Length, 28)]} {n}";
        }

        return unique;
    }
}
