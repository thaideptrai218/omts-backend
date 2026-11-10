using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-03 filters. Item group, item and raft filter the lines; the period defaults to the current month. The single
/// <paramref name="ItemGroup"/> and <paramref name="StatusId"/> stay valid and add to <paramref name="ItemGroups"/> and <paramref name="StatusIds"/>.
/// </summary>
/// <param name="Search">Receipt code or supplier name (contains).</param>
/// <param name="SortBy">code, date, supplier, total or status; newest first when left out.</param>
/// <param name="SortDir">asc or desc.</param>
public sealed record PurchaseReceiptQuery(
    InputItemGroup? ItemGroup = null, Guid? ItemId = null, Guid? RaftId = null, Guid? SupplierId = null,
    int? StatusId = null, DateOnly? From = null, DateOnly? To = null, string? Code = null, int Page = 1, int PageSize = 20,
    string? Search = null, int[]? StatusIds = null, InputItemGroup[]? ItemGroups = null, Guid[]? ZoneIds = null, string? SortBy = null, string? SortDir = null);

/// <param name="LinesTotal">Σ of the lines that match the item group, item and raft filters; the total when none is chosen.</param>
/// <param name="ItemCount">Number of lines on the receipt.</param>
/// <param name="Returned">Σ of its Recorded returns, each less its own discount.</param>
/// <param name="Payable">Total less discount.</param>
/// <param name="Paid">What the payments of the supplier cover of this receipt, oldest receipts first; 0 unless Completed.</param>
/// <param name="ReceivedAt">When the goods were received, at UTC+7.</param>
/// <param name="ReceivedByName">Who took the goods in; null when the receipt has no receiver.</param>
public sealed record PurchaseReceiptRow(
    Guid Id, string Code, DateOnly ReceiptDate, SupplierRef Supplier, int StatusId, string Status, decimal Total, decimal LinesTotal, Guid RecordedByAccountId,
    string SupplierCode, string RecordedByName, int ItemCount, decimal Returned, decimal Discount, decimal Payable, decimal Paid, ZoneRef? Zone,
    string? Notes, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset ReceivedAt, string? ReceivedByName,
    IReadOnlyList<string> ReturnCodes);

/// <summary>
/// PURCHASE-03 step 3: the Completed receipts (matching lines only), the returns dated in the period and the net.
/// <see cref="Count"/> counts every match. <see cref="RaftMaterialCost"/> is always null: a raft's material cost comes from
/// the material used on it (<c>GET api/purchase-receipts/raft-material-costs</c>), not from the receipts.
/// </summary>
public sealed record PurchaseReceiptTotals(
    int Count, int CompletedCount, decimal ReceiptsTotal, decimal ReturnsTotal, decimal NetTotal, decimal? RaftMaterialCost);

/// <param name="Message">MSG-GEN-10 when no receipt matches.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record PurchaseReceiptSearchResult(
    Paged<PurchaseReceiptRow> Page, PurchaseReceiptTotals Totals, DateOnly From, DateOnly To, string? Message, string? MessageCode);

/// <summary>A material usage of the raft, costed at the latest purchase price of its item on its date.</summary>
/// <param name="UnitPrice">Unit price less discount per unit of the latest Completed receipt line of the item received on or before <paramref name="Date"/>; 0 when <paramref name="Unpriced"/>.</param>
/// <param name="Amount">Quantity × unit price, half up to the dong.</param>
/// <param name="Unpriced">True when the item had not been bought by then, so the usage costs 0.</param>
public sealed record RaftUsageCostLine(
    Guid UsageId, DateOnly Date, string RepairReason, Guid ItemId, string ItemCode, string ItemName, string Unit,
    decimal Quantity, decimal UnitPrice, decimal Amount, bool Unpriced);

/// <summary>The material cost of one raft in the period: Σ of the amounts of its material usages dated in it (BR-FARM-05).</summary>
/// <param name="UnpricedCount">How many of <paramref name="Usages"/> are of an item never bought by their date.</param>
public sealed record RaftMaterialCostRow(RaftRef Raft, ZoneRef Zone, decimal MaterialCost, int UnpricedCount, IReadOnlyList<RaftUsageCostLine> Usages);

/// <param name="Message">MSG-GEN-10 when no raft has a material usage in the period.</param>
public sealed record RaftMaterialCostResult(
    DateOnly From, DateOnly To, decimal MaterialCost, IReadOnlyList<RaftMaterialCostRow> Rafts, string? Message, string? MessageCode);

public sealed record ExportedFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// PURCHASE-03 – search purchase receipts with totals, the material cost of rafts from their material usages, the receivers, and the Excel export.
/// The zone of a receipt filters only: the Zone Manager, the Owner and the Accountant (view and export) see every receipt (BR-FARM-08).
/// </summary>
public sealed class PurchaseReceiptQueryService(
    IPurchaseReceiptRepository receipts,
    IRaftRepository rafts,
    IAccountRepository accounts,
    IRaftMaterialCostQuery materialCosts,
    SupplierPaymentAllocationService allocations,
    ISpreadsheetWriter spreadsheets,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    public async Task<Result<PurchaseReceiptSearchResult>> SearchAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // Paging and totals are computed by the database; the page number is capped so the offset cannot overflow.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / 100), Math.Clamp(query.PageSize, 1, 100));
        var (found, totals) = await receipts.PageFiguresAsync(filter.Value!, page, pageSize, cancellationToken);
        // PURCHASE-03.0.E2: the list always has a period, so an empty one is MSG-GEN-10 (filtered), not MSG-GEN-11.
        var empty = totals.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new PurchaseReceiptSearchResult(
            new Paged<PurchaseReceiptRow>(await RowsAsync(found, cancellationToken), page, pageSize, totals.Count),
            Totals(totals),
            filter.Value!.From!.Value, filter.Value.To!.Value,
            empty?.Text,
            empty?.Code);
    }

    /// <summary>The payments that cover the receipt, oldest first, each with the part of it that falls on this receipt.</summary>
    public async Task<Result<IReadOnlyList<ReceiptPaymentView>>> PaymentsAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        return receipt is null
            ? AppError.NotFound
            : Result<IReadOnlyList<ReceiptPaymentView>>.Success(await allocations.PaymentsOfAsync(receipt.Id, receipt.SupplierId, cancellationToken));
    }

    /// <summary>
    /// FARM-04 / BR-FARM-05: the material cost of each raft in the period, from the material used on it, with the usages behind
    /// it. The period is on the usage date; the raft, item and zone filters apply (the zone of the raft); the other filters of
    /// the receipt list do not.
    /// </summary>
    public async Task<Result<RaftMaterialCostResult>> RaftMaterialCostsAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (from, to) = (filter.Value!.From!.Value, filter.Value.To!.Value);
        var usages = await materialCosts.UsagesAsync(
            new RaftUsageCostFilter(from, to, query.RaftId is { } raftId ? [raftId] : null, query.ItemId, filter.Value.ZoneIds?.ToList()), cancellationToken);
        var rows = usages.GroupBy(u => u.RaftId)
            .Select(g =>
            {
                var first = g.First();
                var lines = g.OrderBy(u => u.UsageDate).ThenBy(u => u.ItemCode).ThenBy(u => u.UsageId)
                    .Select(u => new RaftUsageCostLine(
                        u.UsageId, u.UsageDate, u.RepairReason, u.ItemId, u.ItemCode, u.ItemName, u.Unit, u.Quantity,
                        u.UnitPrice ?? 0, PurchaseMoney.LineAmount(u.Quantity, u.UnitPrice), u.UnitPrice is null))
                    .ToList();
                return new RaftMaterialCostRow(
                    new RaftRef(first.RaftId, first.RaftCode), new ZoneRef(first.ZoneId, first.ZoneCode, first.ZoneName),
                    lines.Sum(l => l.Amount), lines.Count(l => l.Unpriced), lines);
            })
            .OrderBy(r => r.Zone.Code).ThenBy(r => r.Raft.Code)
            .ToList();
        var empty = rows.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new RaftMaterialCostResult(from, to, rows.Sum(r => r.MaterialCost), rows, empty?.Text, empty?.Code);
    }

    /// <summary>Who may be named as having received the goods: the Active Owner and staff accounts, by name.</summary>
    public async Task<IReadOnlyList<AccountRef>> ReceiversAsync(CancellationToken cancellationToken) =>
        (await accounts.ListActiveInternalAsync(cancellationToken)).Select(a => new AccountRef(a.Id, a.DisplayName)).ToList();

    /// <summary>
    /// The raft filter of the list and of the material cost: every raft, Discontinued ones included because old receipts name
    /// them. Receipts follow the role only, whatever their zone (BR-FARM-08), so whoever may view them may filter by any raft. It
    /// is how an Accountant, who may not read rafts (§I.4.4 FARM-04 No), fills a filter of PURCHASE-03 (R), so it holds id and
    /// codes only.
    /// </summary>
    public async Task<IReadOnlyList<RaftLookupItem>> RaftLookupAsync(CancellationToken cancellationToken) =>
        await rafts.ListForLookupAsync(PermittedZones.Everywhere, cancellationToken);

    /// <summary>PURCHASE-03.2: the receipts and the totals shown, as an .xlsx file (Export: Zone Manager and Owner).</summary>
    public async Task<Result<ExportedFile>> ExportAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var rows = await RowsAsync(await receipts.ListFiguresAsync(filter.Value!, null, cancellationToken), cancellationToken);
        var totals = Totals(await receipts.TotalsAsync(filter.Value!, cancellationToken));
        var sheetRows = rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Code, r.ReceiptDate.ToDateTime(TimeOnly.MinValue), r.Supplier.Name, r.Status, r.Total, r.LinesTotal,
        ]).ToList();
        sheetRows.Add(["Completed receipts", null, null, $"{totals.CompletedCount} / {totals.Count}", null, totals.ReceiptsTotal]);
        sheetRows.Add(["Purchase returns in the period", null, null, null, null, totals.ReturnsTotal]);
        sheetRows.Add(["Net total", null, null, null, null, totals.NetTotal]);

        try
        {
            var content = spreadsheets.Write([new SpreadsheetSheet(
                "Purchase receipts", ["Code", "Date received", "Supplier", "Status", "Total", "Lines matching the filters"], sheetRows)]);
            var (from, to) = (filter.Value!.From!.Value, filter.Value.To!.Value);
            return new ExportedFile($"purchase-receipts-{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    /// <summary>The filter with its period (<see cref="PurchasePeriod"/>, PURCHASE-03 step 1).</summary>
    private async Task<Result<PurchaseReceiptFilter>> FilterAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        if (query.RaftId is { } raftId && (await rafts.GetByIdsAsync([raftId], cancellationToken)).Count == 0)
        {
            return AppError.Invalid("Raft");
        }

        var (sort, known) = query.SortBy switch
        {
            null or "date" => (PurchaseReceiptSort.Date, true),
            "code" => (PurchaseReceiptSort.Code, true),
            "supplier" => (PurchaseReceiptSort.Supplier, true),
            "total" => (PurchaseReceiptSort.Total, true),
            "status" => (PurchaseReceiptSort.Status, true),
            _ => (PurchaseReceiptSort.Date, false),
        };
        if (!known)
        {
            return AppError.Invalid("Sort by");
        }

        if (query.SortDir is not (null or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction");
        }

        var period = PurchasePeriod.Resolve(query.From, query.To, timeProvider.Today());
        if (!period.IsSuccess)
        {
            return period.Error!;
        }

        var (from, to) = period.Value;
        var statusIds = Merged(query.StatusId, query.StatusIds);
        var itemGroups = Merged(query.ItemGroup, query.ItemGroups);
        return new PurchaseReceiptFilter(
            itemGroups is null ? query.ItemGroup : null, query.ItemId, query.RaftId, query.SupplierId, statusIds is null ? query.StatusId : null, from, to, query.Code,
            query.Search, statusIds, itemGroups, query.ZoneIds is { Length: > 0 } ? query.ZoneIds : null,
            sort, query.SortDir == "desc" || (query.SortDir is null && query.SortBy is null));
    }

    /// <summary>The many-valued filter with the single one added; null when the many-valued filter is not used.</summary>
    private static IReadOnlyList<T>? Merged<T>(T? single, T[]? many) where T : struct =>
        many is { Length: > 0 } ? (single is { } one ? many.Append(one).Distinct().ToList() : many) : null;

    private async Task<IReadOnlyList<PurchaseReceiptRow>> RowsAsync(IReadOnlyList<ReceiptFigureRow> found, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync(
            found.Select(r => r.RecordedByAccountId).Concat(found.Select(r => r.ReceivedByAccountId).OfType<Guid>()).Distinct().ToList(), cancellationToken);
        var paid = await allocations.PaidByReceiptAsync(found.Select(r => r.SupplierId).Distinct().ToList(), cancellationToken);
        return found.Select(r => new PurchaseReceiptRow(
            r.Id, r.Code, r.ReceiptDate, new SupplierRef(r.SupplierId, r.SupplierName, r.SupplierPhone, r.SupplierCode),
            r.StatusId, statusCatalog.DisplayNameOf(r.StatusId), r.Total, r.LinesTotal, r.RecordedByAccountId,
            r.SupplierCode, names.GetValueOrDefault(r.RecordedByAccountId) ?? string.Empty, r.ItemCount, r.Returned, r.Discount, r.Payable, paid.GetValueOrDefault(r.Id),
            r.ZoneId is { } zoneId ? new ZoneRef(zoneId, r.ZoneCode!, r.ZoneName!) : null,
            r.Notes, r.CompletedAt, r.CreatedAt, r.UpdatedAt,
            r.ReceivedAt, r.ReceivedByAccountId is { } receiverId ? names.GetValueOrDefault(receiverId) : null, r.ReturnCodes)).ToList();
    }

    private static PurchaseReceiptTotals Totals(ReceiptListTotals t) =>
        new(t.Count, t.CompletedCount, t.ReceiptsTotal, t.ReturnsTotal, t.NetTotal, RaftMaterialCost: null);
}
