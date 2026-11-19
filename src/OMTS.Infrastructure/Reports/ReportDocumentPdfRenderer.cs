using System.Globalization;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Features.Reports.Documents;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OMTS.Infrastructure.Reports;

/// <summary>
/// A <see cref="ReportDocument"/> as a PDF in the KiotViet layout: A4 landscape, "Ngày lập" top left with the centred title and filter lines, a light blue header row repeated on every page,
/// beige group rows, grey sub-groups, detail rows with thin grey bottom borders, numbers right-aligned with thousand separators, document codes in blue, the footer text centred and
/// "Trang:n/N" on the right. Uses the Noto Sans files embedded in this assembly, never the fonts of the machine.
/// QuestPDF Community license: free for an organisation under USD 1M yearly revenue (https://www.questpdf.com/license/).
/// </summary>
internal sealed class ReportDocumentPdfRenderer : IReportDocumentRenderer
{
    private const string FontFamily = "Noto Sans";
    private const string HeaderFill = "#B3E5FC";
    private const string GroupFill = "#F0EDD6";
    private const string SubGroupFill = "#F2F2F2";
    private const string Grey = "#BFBFBF";
    private const string CodeBlue = "#1565C0";
    private const string TextColor = "#333333";

    static ReportDocumentPdfRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = false;
        foreach (var resource in new[] { "OMTS.Fonts.NotoSans-Regular.ttf", "OMTS.Fonts.NotoSans-Bold.ttf" })
        {
            using var stream = typeof(ReportDocumentPdfRenderer).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The embedded font {resource} is missing.");
            FontManager.RegisterFontFromStream(stream);
        }
    }

    public ReportFileFormat Format => ReportFileFormat.Pdf;

    public string ContentType => "application/pdf";

    public string Extension => "pdf";

    public byte[] Render(ReportDocument document) =>
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(28, Unit.Point);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(8.5f).FontColor(TextColor));
            page.Content().Column(column =>
            {
                column.Item().Element(c => Title(c, document));
                foreach (var section in document.Sections)
                {
                    column.Item().PaddingTop(14).Element(c => Section(c, section));
                }
            });
            page.Footer().PaddingTop(10).Row(row =>
            {
                row.RelativeItem();
                row.RelativeItem(2).AlignCenter().Text(document.FooterText).FontSize(9);
                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(9));
                    text.Span("Trang:");
                    text.CurrentPageNumber();
                    text.Span("/");
                    text.TotalPages();
                });
            });
        })).GeneratePdf();

    private static void Title(IContainer container, ReportDocument document) =>
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Text($"Ngày lập: {document.CreatedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}").FontSize(9);
                row.RelativeItem(3).AlignCenter().Text(document.Title).FontSize(18).Bold().FontColor("#222222");
                row.RelativeItem();
            });
            foreach (var line in document.FilterLines)
            {
                column.Item().PaddingTop(5).AlignCenter().Text(line).FontSize(9.5f);
            }

            column.Item().Height(10);
        });

    private static void Section(IContainer container, ReportSection section) =>
        container.Column(column =>
        {
            if (section.Heading is not null)
            {
                column.Item().PaddingBottom(5).Text(section.Heading).FontSize(11).Bold();
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    foreach (var c in section.Columns)
                    {
                        columns.RelativeColumn((float)c.Width);
                    }
                });
                table.Header(header =>
                {
                    foreach (var c in section.Columns)
                    {
                        var cell = header.Cell().Background(HeaderFill).PaddingVertical(7).PaddingHorizontal(5).AlignMiddle();
                        Align(cell, c.Align == ReportAlign.Left ? ReportAlign.Left : c.Align).Text(c.Title).Bold();
                    }
                });
                foreach (var row in section.Rows)
                {
                    WriteRow(table, section.Columns, row);
                }
            });
        });

    private static void WriteRow(TableDescriptor table, IReadOnlyList<ReportColumn> columns, ReportRow row)
    {
        var isDetail = row.Kind == ReportRowKind.Detail;
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            var value = c < row.Cells.Count ? row.Cells[c] : null;

            // A group label runs across the empty left-aligned cells after it, so it is not squeezed into the first column.
            var span = 1;
            if (!isDetail && c == 0)
            {
                while (c + span < columns.Count && columns[c + span].Align == ReportAlign.Left && (c + span >= row.Cells.Count || row.Cells[c + span] is null))
                {
                    span++;
                }
            }

            var cell = table.Cell().ColumnSpan((uint)span).Element(e => isDetail
                ? e.BorderBottom(0.5f).BorderColor(Grey).PaddingVertical(5).PaddingHorizontal(5).AlignMiddle()
                : e.Background(row.Kind == ReportRowKind.Group ? GroupFill : SubGroupFill).PaddingVertical(8).PaddingHorizontal(5).AlignMiddle());
            var text = Align(cell, column.Align).Text(ReportCellText.Format(value, column.Format));
            if (!isDetail)
            {
                text.Bold();
            }
            else if (column.IsCode)
            {
                text.FontColor(CodeBlue);
            }

            c += span - 1;
        }
    }

    private static IContainer Align(IContainer container, ReportAlign align) => align switch
    {
        ReportAlign.Right => container.AlignRight(),
        ReportAlign.Center => container.AlignCenter(),
        _ => container.AlignLeft(),
    };
}
