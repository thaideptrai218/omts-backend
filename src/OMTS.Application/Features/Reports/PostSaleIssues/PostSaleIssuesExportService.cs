using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Reports.PostSaleIssues;

/// <summary>
/// REPORT-03.3: the figures shown, with the period and the choices, as an .xlsx file. The same selection as the screen (so the same checks), but with the Export right. The sheet "Post-sale
/// issues" holds the groups and a Total row (a figure that does not apply to a lot is left empty); the sheet "Choices" the period, the grouping and the zones and customers chosen. Text is
/// never a formula. An empty selection gives the headers and a zero total row, as REPORT-01. Changes nothing.
/// </summary>
public sealed class PostSaleIssuesExportService(PostSaleIssuesService report, ISpreadsheetWriter spreadsheets)
{
    public const string FiguresSheetName = "Post-sale issues";

    public async Task<Result<ExportedFile>> ExportAsync(PostSaleIssuesQuery request, CancellationToken cancellationToken)
    {
        var found = await report.BuildAsync(request, PermissionAction.Export, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var shown = found.Value!;
        var fileName = $"post-sale-issues-{shown.GroupBy}-{shown.From:yyyyMMdd}-{shown.To:yyyyMMdd}.xlsx";
        return ReportExportFile.Write(spreadsheets, fileName, [FiguresSheet(shown), ChoicesSheet(shown)]);
    }

    private static SpreadsheetSheet FiguresSheet(PostSaleIssuesReport shown)
    {
        var headers = new List<string>
        {
            GroupHeader(shown.GroupBy), "Claims sent", "Claims awaiting response", "Claims rejected", "Claims accepted", "Sales returns", "Returned kg", "Discarded kg", "Returns value",
            "Price cuts", "Price cuts amount", "Cancelled invoices", "Cancelled total",
        };
        var rows = shown.Groups.Append(shown.Totals with { Label = "Total" }).Select(Row).ToList();
        return new SpreadsheetSheet(FiguresSheetName, headers, rows);
    }

    private static IReadOnlyList<object?> Row(PostSaleGroup g) =>
        [g.Label, g.ClaimsSent, g.ClaimsAwaiting, g.ClaimsRejected, g.ClaimsAccepted, g.ReturnsCount, g.ReturnedKg, g.DiscardedKg, g.ReturnsValue, g.PriceCutsCount, g.PriceCutsAmount, g.CancelledCount, g.CancelledTotal];

    private static SpreadsheetSheet ChoicesSheet(PostSaleIssuesReport shown) => ReportExportFile.ChoicesSheet(
        shown.From, shown.To, shown.Filters.Zones, shown.Filters.Customers, [], [("Grouping", GroupHeader(shown.GroupBy))]);

    private static string GroupHeader(string groupBy) => char.ToUpperInvariant(groupBy[0]) + groupBy[1..];
}
