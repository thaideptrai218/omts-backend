using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>Reads purchase returns: the list with its total, the Excel export and the detail. Every status is listed; only Recorded returns add to the total.</summary>
public sealed class PurchaseReturnQueryService(
    IPurchaseReceiptRepository receipts,
    IAccountRepository accounts,
    ISpreadsheetWriter spreadsheets,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    public async Task<Result<PurchaseReturnView>> GetAsync(Guid returnId, CancellationToken cancellationToken) =>
        await receipts.GetReturnAsync(returnId, cancellationToken) is null ? AppError.NotFound : await ViewAsync(returnId, cancellationToken);

    public async Task<PurchaseReturnView> ViewAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var found = await receipts.GetReturnAsync(returnId, cancellationToken) ?? throw new InvalidOperationException($"Return {returnId} not found.");
        var names = await accounts.GetDisplayNamesAsync([found.RecordedByAccountId], cancellationToken);
        var refunds = await receipts.RefundsOfReturnAsync(returnId, cancellationToken);
        var value = found.Lines.Sum(l => l.ReturnValue);
        return new PurchaseReturnView(
            found.Id, found.Code, found.ReceiptId, found.Receipt?.Code,
            found.Receipt is { } receipt ? new ReturnReceiptRef(receipt.Id, receipt.Code) : null,
            new SupplierRef(found.Supplier.Id, found.Supplier.Name, found.Supplier.PhoneNumber),
            found.Zone is { } zone ? new ZoneRef(zone.Id, zone.Code, zone.Name) : null,
            found.ReturnDate, found.Reason, found.Discount, found.StatusId, statusCatalog.DisplayNameOf(found.StatusId),
            found.CancelledAt, found.CancelReason, new AccountRef(found.RecordedByAccountId, names.GetValueOrDefault(found.RecordedByAccountId) ?? string.Empty),
            value, value - found.Discount,
            found.Lines.Select(l => new PurchaseReturnLineView(l.ReceiptLineId, l.ItemId, l.Item.Code, l.Item.Name, l.Unit, l.Quantity, l.UnitPrice, l.ReturnValue)).ToList(),
            refunds.Select(p => new ReturnRefundView(p.Id, p.Code, p.PaymentDate, p.Amount, p.Method, p.BankReference, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), p.CancelledAt)).ToList(),
            RowVersionGuard.Encode(found.RowVersion),
            found.ReturnedAt);
    }

    public async Task<Result<PurchaseReturnSearchResult>> SearchAsync(PurchaseReturnQuery query, CancellationToken cancellationToken)
    {
        var filter = Filter(query);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / 100), Math.Clamp(query.PageSize, 1, 100));
        var (found, total, totalValue) = await receipts.SearchReturnsAsync(filter.Value!, page, pageSize, cancellationToken);
        var empty = total == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new PurchaseReturnSearchResult(
            new Paged<PurchaseReturnRow>(await RowsAsync(found, cancellationToken), page, pageSize, total),
            totalValue, filter.Value!.From, filter.Value.To, empty?.Text, empty?.Code);
    }

    public async Task<Result<ExportedFile>> ExportAsync(PurchaseReturnQuery query, CancellationToken cancellationToken)
    {
        var filter = Filter(query);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var rows = await RowsAsync(await receipts.ListReturnsAsync(filter.Value!, cancellationToken), cancellationToken);
        var sheetRows = rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Code, r.ReturnDate.ToDateTime(TimeOnly.MinValue), r.Supplier.Name, r.ReceiptCode, r.Zone?.Name, r.Status, r.Value, r.Discount, r.NetValue, r.Refunded,
        ]).ToList();
        sheetRows.Add(["Recorded returns", null, null, null, null, null, null, null,
            rows.Where(r => r.StatusId == StatusIds.PurchaseReturn.Recorded).Sum(r => r.NetValue), null]);
        try
        {
            var content = spreadsheets.Write([new SpreadsheetSheet(
                "Purchase returns", ["Code", "Return date", "Supplier", "Receipt", "Zone", "Status", "Value", "Discount", "Net value", "Paid back"], sheetRows)]);
            return new ExportedFile($"purchase-returns-{filter.Value!.From:yyyyMMdd}-{filter.Value.To:yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private Result<PurchaseReturnFilter> Filter(PurchaseReturnQuery query)
    {
        var (sort, known) = query.SortBy switch
        {
            null or "date" => (PurchaseReturnSort.Date, true),
            "code" => (PurchaseReturnSort.Code, true),
            "supplier" => (PurchaseReturnSort.Supplier, true),
            "total" => (PurchaseReturnSort.Total, true),
            "status" => (PurchaseReturnSort.Status, true),
            _ => (PurchaseReturnSort.Date, false),
        };
        if (!known)
        {
            return AppError.Invalid("Sort by");
        }

        if (query.SortDir is not (null or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction");
        }

        if (query.Source is not (null or "receipt" or "standalone"))
        {
            return AppError.Invalid("Source");
        }

        var period = PurchasePeriod.Resolve(query.From, query.To, timeProvider.Today());
        if (!period.IsSuccess)
        {
            return period.Error!;
        }

        var (from, to) = period.Value;
        return new PurchaseReturnFilter(
            query.Search, from, to, query.SupplierId, NullIfEmpty(query.StatusIds), NullIfEmpty(query.ZoneIds), sort,
            query.SortDir == "desc" || (query.SortDir is null && query.SortBy is null),
            query.Source is null ? null : query.Source == "receipt");
    }

    private static IReadOnlyList<T>? NullIfEmpty<T>(T[]? values) => values is { Length: > 0 } ? values : null;

    private async Task<IReadOnlyList<PurchaseReturnRow>> RowsAsync(IReadOnlyList<PurchaseReturnFigureRow> found, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync(found.Select(r => r.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        return found.Select(r => new PurchaseReturnRow(
            r.Id, r.Code, r.ReturnDate, r.ReceiptId, r.ReceiptCode,
            new SupplierRef(r.SupplierId, r.SupplierName, r.SupplierPhone), r.SupplierCode,
            r.ZoneId is { } zoneId ? new ZoneRef(zoneId, r.ZoneCode!, r.ZoneName!) : null,
            r.Reason, r.StatusId, statusCatalog.DisplayNameOf(r.StatusId),
            new AccountRef(r.RecordedByAccountId, names.GetValueOrDefault(r.RecordedByAccountId) ?? string.Empty),
            r.Value, r.Discount, r.Value - r.Discount, r.LineCount, r.Refunded, r.CancelledAt, r.CancelReason, r.CreatedAt, r.UpdatedAt,
            RowVersionGuard.Encode(r.RowVersion), r.ReturnedAt)).ToList();
    }
}
