using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Reports.Purchases;

/// <summary>
/// The figures shown as an .xlsx file <c>purchases-{groupBy}-{from}-{to}.xlsx</c>: the sheet "Purchases" (groups and a Total row) and the sheet "Choices". The same selection as the screen,
/// with the Export right. Changes nothing.
/// </summary>
public sealed class PurchaseReportExportService(PurchaseReportService report, ISpreadsheetWriter spreadsheets)
{
    public const string FiguresSheetName = "Purchases";

    public async Task<Result<ExportedFile>> ExportAsync(PurchaseReportQuery request, CancellationToken cancellationToken)
    {
        var found = await report.BuildAsync(request, PermissionAction.Export, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var shown = found.Value!;
        var byItem = shown.GroupBy == "item";
        var headers = new List<string> { byItem ? "Item" : "Supplier" };
        if (byItem)
        {
            headers.AddRange(["Code", "Unit"]);
        }

        headers.AddRange(["Receipts", "Returns", "Received value", "Returned value", "Net value", "VAT", "Goods value"]);
        if (byItem)
        {
            headers.AddRange(["Received quantity", "Returned quantity"]);
        }

        IReadOnlyList<object?> Row(
            string label, string? code, string? unit, int receipts, int returns, decimal received, decimal returned, decimal net, decimal vat, decimal goods, decimal? receivedQty, decimal? returnedQty)
        {
            var cells = new List<object?> { label };
            if (byItem)
            {
                cells.AddRange([code, unit]);
            }

            cells.AddRange([receipts, returns, received, returned, net, vat, goods]);
            if (byItem)
            {
                cells.AddRange([receivedQty, returnedQty]);
            }

            return cells;
        }

        var rows = shown.Groups.Select(g => Row(g.Label, g.Code, g.Unit, g.ReceiptCount, g.ReturnCount, g.ReceivedValue, g.ReturnedValue, g.NetValue, g.VatAmount, g.GoodsValue, g.ReceivedQuantity, g.ReturnedQuantity)).ToList();
        var t = shown.Totals;
        rows.Add(Row("Total", null, null, t.ReceiptCount, t.ReturnCount, t.ReceivedValue, t.ReturnedValue, t.NetValue, t.VatAmount, t.GoodsValue, t.ReceivedQuantity, t.ReturnedQuantity));
        var suppliers = shown.Filters.Suppliers.Count == 0 ? ReportExportFile.NoneChosen : string.Join(", ", shown.Filters.Suppliers.Select(s => s.Name));
        var choices = ReportExportFile.ChoicesSheet(
            shown.From, shown.To, [], [], [], [("Grouping", shown.GroupBy), ("Kind", shown.Filters.Kind), ("Suppliers", suppliers)]);
        var fileName = $"purchases-{shown.GroupBy}-{shown.From:yyyyMMdd}-{shown.To:yyyyMMdd}.xlsx";
        return ReportExportFile.Write(spreadsheets, fileName, [new SpreadsheetSheet(FiguresSheetName, headers, rows), choices]);
    }
}
