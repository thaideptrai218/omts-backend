using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Reports.PriceTrends;

/// <summary>
/// REPORT-02.3: the figures shown, with the period and the choices, as an .xlsx file. The same selection as the screen (so the same checks), but with the Export right. The sheet "Price trends"
/// has one row per product and period; the sheet "Choices" the period, the time unit and the zones and products chosen. Text is never a formula. An empty selection gives the headers only.
/// Changes nothing.
/// </summary>
public sealed class PriceTrendsExportService(PriceTrendsService report, ISpreadsheetWriter spreadsheets)
{
    public const string FiguresSheetName = "Price trends";

    public async Task<Result<ExportedFile>> ExportAsync(PriceTrendsQuery request, CancellationToken cancellationToken)
    {
        var found = await report.BuildAsync(request, PermissionAction.Export, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var shown = found.Value!;
        var fileName = $"price-trends-{shown.Unit}-{shown.From:yyyyMMdd}-{shown.To:yyyyMMdd}.xlsx";
        return ReportExportFile.Write(spreadsheets, fileName, [FiguresSheet(shown), ChoicesSheet(shown)]);
    }

    private static SpreadsheetSheet FiguresSheet(PriceTrendsReport shown)
    {
        var rows = shown.Series
            .SelectMany(s => s.Periods.Select(p => (IReadOnlyList<object?>)[s.ProductName, p.Label, p.SoldKg, p.AveragePrice, p.LowestPrice, p.HighestPrice]))
            .ToList();
        return new SpreadsheetSheet(FiguresSheetName, ["Product", "Period", "Sold kg", "Average unit price", "Lowest unit price", "Highest unit price"], rows);
    }

    private static SpreadsheetSheet ChoicesSheet(PriceTrendsReport shown) => ReportExportFile.ChoicesSheet(
        shown.From, shown.To, shown.Filters.Zones, [], shown.Filters.Products, [("Time unit", char.ToUpperInvariant(shown.Unit[0]) + shown.Unit[1..])]);
}
