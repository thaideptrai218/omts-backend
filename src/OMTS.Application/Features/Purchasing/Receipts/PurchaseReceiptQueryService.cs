using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>PURCHASE-03 filters. Item group, item and raft filter the lines; the period defaults to the current month.</summary>
public sealed record PurchaseReceiptQuery(
    InputItemGroup? ItemGroup = null, Guid? ItemId = null, Guid? RaftId = null, Guid? SupplierId = null,
    int? StatusId = null, DateOnly? From = null, DateOnly? To = null, string? Code = null, int Page = 1, int PageSize = 20);

/// <param name="LinesTotal">Σ of the lines that match the item group, item and raft filters; the total when none is chosen.</param>
public sealed record PurchaseReceiptRow(
    Guid Id, string Code, DateOnly ReceiptDate, SupplierRef Supplier, int StatusId, string Status, decimal Total, decimal LinesTotal, Guid RecordedByAccountId);

/// <summary>
/// PURCHASE-03 step 3: the Completed receipts (matching lines only), the returns dated in the period and the net. With a raft
/// filter, <see cref="RaftMaterialCost"/> is the net of its Material lines (BR-FARM-05). <see cref="Count"/> counts every match.
/// </summary>
public sealed record PurchaseReceiptTotals(
    int Count, int CompletedCount, decimal ReceiptsTotal, decimal ReturnsTotal, decimal NetTotal, decimal? RaftMaterialCost);

/// <param name="Message">MSG-GEN-10 when no receipt matches.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record PurchaseReceiptSearchResult(
    Paged<PurchaseReceiptRow> Page, PurchaseReceiptTotals Totals, DateOnly From, DateOnly To, string? Message, string? MessageCode);

public sealed record RaftCostLine(
    Guid ReceiptId, string ReceiptCode, DateOnly ReceiptDate, string SupplierName, Guid LineId, int LineNumber,
    Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal UnitPrice, decimal LineAmount);

public sealed record RaftCostReturn(
    Guid ReturnId, string ReturnCode, DateOnly ReturnDate, Guid ReceiptId, string ReceiptCode, Guid LineId, int LineNumber,
    string ItemName, string Unit, decimal Quantity, decimal ReturnValue);

/// <summary>The material cost of one raft in the period: its Material lines received, minus their returns dated in it (BR-FARM-05).</summary>
public sealed record RaftMaterialCostRow(
    RaftRef Raft, ZoneRef Zone, decimal Purchased, decimal Returned, decimal MaterialCost, IReadOnlyList<RaftCostLine> Lines, IReadOnlyList<RaftCostReturn> Returns);

/// <param name="Message">MSG-GEN-10 when no raft has a material cost in the period.</param>
public sealed record RaftMaterialCostResult(
    DateOnly From, DateOnly To, decimal MaterialCost, IReadOnlyList<RaftMaterialCostRow> Rafts, string? Message, string? MessageCode);

public sealed record ExportedFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// PURCHASE-03 – search purchase receipts with totals, the material cost of rafts per line and period, and the Excel export.
/// Receipts have no zone: the Zone Manager, the Owner and the Accountant (view and export) see every receipt (BR-FARM-08).
/// </summary>
public sealed class PurchaseReceiptQueryService(
    IPurchaseReceiptRepository receipts,
    IRaftRepository rafts,
    ISpreadsheetWriter spreadsheets,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    /// <summary>Longest list, export and cost period, so one request never reads the whole history.</summary>
    public const int MaxPeriodYears = 5;

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
            new Paged<PurchaseReceiptRow>(found.Select(Row).ToList(), page, pageSize, totals.Count),
            Totals(totals),
            filter.Value!.From!.Value, filter.Value.To!.Value,
            empty?.Text,
            empty?.Code);
    }

    /// <summary>FARM-04 / PURCHASE-03: the material cost of each raft in the period with the lines and returns behind it.</summary>
    public async Task<Result<RaftMaterialCostResult>> RaftMaterialCostsAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (lines, returns) = await receipts.RaftMaterialCostRowsAsync(filter.Value!, cancellationToken);
        var rows = lines.Select(l => (l.RaftId, l.RaftCode, l.ZoneId, l.ZoneCode, l.ZoneName))
            .Concat(returns.Select(r => (r.RaftId, r.RaftCode, r.ZoneId, r.ZoneCode, r.ZoneName)))
            .DistinctBy(r => r.RaftId)
            .Select(raft =>
            {
                var raftLines = lines.Where(l => l.RaftId == raft.RaftId).OrderBy(l => l.ReceiptDate).ThenBy(l => l.ReceiptCode).ThenBy(l => l.LineNumber)
                    .Select(l => new RaftCostLine(
                        l.ReceiptId, l.ReceiptCode, l.ReceiptDate, l.SupplierName, l.LineId, l.LineNumber, l.ItemId, l.ItemCode, l.ItemName, l.Unit,
                        l.Quantity, l.UnitPrice, l.LineAmount))
                    .ToList();
                var raftReturns = returns.Where(r => r.RaftId == raft.RaftId).OrderBy(r => r.ReturnDate).ThenBy(r => r.ReturnCode)
                    .Select(r => new RaftCostReturn(r.ReturnId, r.ReturnCode, r.ReturnDate, r.ReceiptId, r.ReceiptCode, r.LineId, r.LineNumber, r.ItemName, r.Unit, r.Quantity, r.ReturnValue))
                    .ToList();
                var (purchased, returned) = (raftLines.Sum(l => l.LineAmount), raftReturns.Sum(r => r.ReturnValue));
                return new RaftMaterialCostRow(
                    new RaftRef(raft.RaftId, raft.RaftCode), new ZoneRef(raft.ZoneId, raft.ZoneCode, raft.ZoneName),
                    purchased, returned, purchased - returned, raftLines, raftReturns);
            })
            .OrderBy(r => r.Zone.Code).ThenBy(r => r.Raft.Code)
            .ToList();
        var empty = rows.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new RaftMaterialCostResult(filter.Value!.From!.Value, filter.Value.To!.Value, rows.Sum(r => r.MaterialCost), rows, empty?.Text, empty?.Code);
    }

    /// <summary>PURCHASE-03.2: the receipts and the totals shown, as an .xlsx file (Export: Zone Manager and Owner).</summary>
    public async Task<Result<ExportedFile>> ExportAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var rows = (await receipts.ListFiguresAsync(filter.Value!, null, cancellationToken)).Select(Row).ToList();
        var totals = Totals(await receipts.TotalsAsync(filter.Value!, cancellationToken));
        var sheetRows = rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Code, r.ReceiptDate.ToDateTime(TimeOnly.MinValue), r.Supplier.Name, r.Status, r.Total, r.LinesTotal,
        ]).ToList();
        sheetRows.Add(["Completed receipts", null, null, $"{totals.CompletedCount} / {totals.Count}", null, totals.ReceiptsTotal]);
        sheetRows.Add(["Purchase returns in the period", null, null, null, null, totals.ReturnsTotal]);
        sheetRows.Add(["Net total", null, null, null, null, totals.NetTotal]);
        if (totals.RaftMaterialCost is { } raftCost)
        {
            sheetRows.Add(["Raft material cost", null, null, null, null, raftCost]);
        }

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

    /// <summary>
    /// The period defaults to the current month (PURCHASE-03 step 1): a missing start is the first day of the month of the end,
    /// a missing end the last day of the current month. At most five years; a start after the end is MSG-GEN-01.
    /// </summary>
    private async Task<Result<PurchaseReceiptFilter>> FilterAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        if (query.RaftId is { } raftId && (await rafts.GetByIdsAsync([raftId], cancellationToken)).Count == 0)
        {
            return AppError.Invalid("Raft");
        }

        var today = timeProvider.Today();
        var from = query.From ?? FirstOfMonth(query.To ?? today);
        var to = query.To ?? FirstOfMonth(today).AddMonths(1).AddDays(-1);
        if (from > to)
        {
            return AppError.Invalid("From date");
        }

        if (to > from.AddYears(MaxPeriodYears))
        {
            return AppError.Invalid("To date", $"The period is at most {MaxPeriodYears} years.");
        }

        return new PurchaseReceiptFilter(query.ItemGroup, query.ItemId, query.RaftId, query.SupplierId, query.StatusId, from, to, query.Code);
    }

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    private PurchaseReceiptRow Row(ReceiptFigureRow r) => new(
        r.Id, r.Code, r.ReceiptDate, new SupplierRef(r.SupplierId, r.SupplierName, r.SupplierPhone),
        r.StatusId, statusCatalog.DisplayNameOf(r.StatusId), r.Total, r.LinesTotal, r.RecordedByAccountId);

    private static PurchaseReceiptTotals Totals(ReceiptListTotals t) =>
        new(t.Count, t.CompletedCount, t.ReceiptsTotal, t.ReturnsTotal, t.NetTotal, t.RaftMaterialCost);
}
