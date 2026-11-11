using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Reports.SalesPerformance;

/// <summary>
/// REPORT-01.4: the figures shown, with the period and the choices, as an .xlsx file. The same selection as the screen (so the same checks), but with the Export right. The sheet
/// "Sales performance" holds the groups and a Total row; the sheet "Choices" the period, the grouping and the zones, customers and products chosen. Text is never a formula. An empty
/// selection gives the headers and a zero total row, as the other exports. Changes nothing.
/// </summary>
public sealed class SalesPerformanceExportService(SalesPerformanceService report, ISpreadsheetWriter spreadsheets)
{
    public const string FiguresSheetName = "Sales performance";

    public async Task<Result<ExportedFile>> ExportAsync(SalesPerformanceQuery request, CancellationToken cancellationToken)
    {
        var found = await report.BuildAsync(request, PermissionAction.Export, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var shown = found.Value!;
        var fileName = $"sales-performance-{shown.GroupBy}-{shown.From:yyyyMMdd}-{shown.To:yyyyMMdd}.xlsx";
        return ReportExportFile.Write(spreadsheets, fileName, [FiguresSheet(shown), ChoicesSheet(shown)]);
    }

    private static SpreadsheetSheet FiguresSheet(SalesPerformanceReport shown)
    {
        var withMoney = shown.Totals.NetMoneyReceived is not null;
        var headers = new List<string> { GroupHeader(shown.GroupBy), "Invoices", "Invoiced kg", "Revenue", "Price cuts", "Sales returns", "Net revenue", "Sold kg" };
        if (withMoney)
        {
            headers.AddRange(["Net money received", "Debt at end of period"]);
        }

        var rows = shown.Groups
            .Select(g => Row(g.Label, g.InvoiceCount, g.InvoicedKg, g.Revenue, g.PriceCuts, g.SalesReturns, g.NetRevenue, g.SoldKg, g.NetMoneyReceived, g.DebtAtEnd, withMoney))
            .ToList();
        var t = shown.Totals;
        rows.Add(Row("Total", t.InvoiceCount, t.InvoicedKg, t.Revenue, t.PriceCuts, t.SalesReturns, t.NetRevenue, t.SoldKg, t.NetMoneyReceived, t.DebtAtEnd, withMoney));
        return new SpreadsheetSheet(FiguresSheetName, headers, rows);
    }

    private static IReadOnlyList<object?> Row(
        string label, int invoices, decimal kg, decimal revenue, decimal priceCuts, decimal returns, decimal net, decimal soldKg, decimal? received, decimal? debt, bool withMoney)
    {
        var cells = new List<object?> { label, invoices, kg, revenue, priceCuts, returns, net, soldKg };
        if (withMoney)
        {
            cells.AddRange([received, debt]);
        }

        return cells;
    }

    private static SpreadsheetSheet ChoicesSheet(SalesPerformanceReport shown) => ReportExportFile.ChoicesSheet(
        shown.From, shown.To, shown.Filters.Zones, shown.Filters.Customers, shown.Filters.Products, [("Grouping", GroupHeader(shown.GroupBy))]);

    private static string GroupHeader(string groupBy) => char.ToUpperInvariant(groupBy[0]) + groupBy[1..];
}
