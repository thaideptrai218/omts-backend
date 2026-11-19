using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Reports.ProductStock;

/// <summary>
/// The figures shown as an .xlsx file <c>product-stock-{groupBy}-{from}-{to}.xlsx</c>: the sheet "Product stock" (groups and a Total row) and the sheet "Choices". The same selection as the
/// screen, with the Export right. Changes nothing.
/// </summary>
public sealed class ProductStockExportService(ProductStockService report, ISpreadsheetWriter spreadsheets)
{
    public const string FiguresSheetName = "Product stock";

    public async Task<Result<ExportedFile>> ExportAsync(ProductStockQuery request, CancellationToken cancellationToken)
    {
        var found = await report.BuildAsync(request, PermissionAction.Export, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var shown = found.Value!;
        var headers = new List<string> { shown.GroupBy == "product" ? "Product" : "Category", "Code", "Opening kg", "Received kg", "Sold kg", "Disposed kg", "Adjustment kg", "Closing kg", "Closing value (list price)" };
        var rows = shown.Groups
            .Select(g => (IReadOnlyList<object?>)[g.Label, g.Code, g.OpeningKg, g.ReceivedKg, g.SoldKg, g.DisposedKg, g.AdjustmentKg, g.ClosingKg, g.ClosingValue])
            .ToList();
        var t = shown.Totals;
        rows.Add(["Total", null, t.OpeningKg, t.ReceivedKg, t.SoldKg, t.DisposedKg, t.AdjustmentKg, t.ClosingKg, t.ClosingValue]);
        var choices = ReportExportFile.ChoicesSheet(
            shown.From, shown.To, [], [], shown.Filters.Products,
            [("Grouping", shown.GroupBy), ("Categories", shown.Filters.Categories.Count == 0 ? ReportExportFile.NoneChosen : string.Join(", ", shown.Filters.Categories.Select(c => c.Name)))]);
        var fileName = $"product-stock-{shown.GroupBy}-{shown.From:yyyyMMdd}-{shown.To:yyyyMMdd}.xlsx";
        return ReportExportFile.Write(spreadsheets, fileName, [new SpreadsheetSheet(FiguresSheetName, headers, rows), choices]);
    }
}
