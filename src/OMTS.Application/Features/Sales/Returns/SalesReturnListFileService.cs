using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>
/// The sales return list as .xlsx: every return the filters match in the list order, with the same zone rule as the list (SALES_RETURNS View in the zone of the invoice), and a row of the sums of the
/// returns that stand. Text cells are written as text.
/// </summary>
public sealed class SalesReturnListFileService(SalesReturnListService list, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    /// <summary>The most rows one file holds; a wider period is exported in parts.</summary>
    public const int MaxRows = 10_000;

    private static readonly string[] Headers =
    [
        "Return code", "Return date", "Recorded at", "Invoice", "Sales order", "Customer code", "Customer", "Zone", "Value", "Back for sale kg", "Thrown away kg", "Lines", "Status", "Recorded by",
    ];

    public async Task<Result<ExportedFile>> ExportAsync(SalesReturnListQuery query, CancellationToken cancellationToken)
    {
        // One read of the first MaxRows returns, as the orders and disposals exports do.
        var found = await list.ExportRowsAsync(query, MaxRows, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var value = found.Value!;
        var rows = value.Items.Select(Row).ToList();
        rows.Add(["Total of the returns that stand", null, null, null, null, null, null, null, value.Totals.Value, value.Totals.BackForSaleKg, value.Totals.DiscardedKg, null, null, null]);

        try
        {
            var sheet = new SpreadsheetSheet("Sales returns", Headers, rows);
            return new ExportedFile($"sales-returns-{timeProvider.Today():yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, spreadsheets.Write([sheet]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private static IReadOnlyList<object?> Row(SalesReturnListItem item) =>
    [
        item.Code, item.ReturnDate.ToDateTime(TimeOnly.MinValue), item.RecordedAt?.ToOffset(BusinessClock.UtcOffset).DateTime, item.InvoiceCode, item.SalesOrder?.Code, item.Customer?.Code, item.Customer?.Name,
        item.ZoneCode, item.Value, item.BackForSaleKg, item.DiscardedKg, item.LineCount, item.Status, item.RecordedByName,
    ];
}
