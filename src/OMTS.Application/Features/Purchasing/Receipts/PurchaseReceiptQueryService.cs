using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

public sealed record PurchaseReceiptQuery(
    InputItemGroup? ItemGroup = null, Guid? ItemId = null, Guid? ZoneId = null, Guid? RaftId = null, Guid? SupplierId = null,
    int? StatusId = null, DateOnly? From = null, DateOnly? To = null, string? Code = null, int Page = 1, int PageSize = 20);

public sealed record PurchaseReceiptRow(
    Guid Id, string Code, DateOnly? ReceiptDate, SupplierRef Supplier, ZoneRef Zone, RaftRef? Raft, int StatusId, string Status,
    decimal Subtotal, decimal Discount, decimal Payable, decimal Returned, decimal Paid, decimal Outstanding);

/// <summary>Money totals count Completed receipts only; <see cref="Count"/> is every matching receipt.</summary>
public sealed record PurchaseReceiptTotals(
    int Count, int CompletedCount, decimal Subtotal, decimal Discount, decimal Payable, decimal Returned, decimal Paid, decimal Outstanding);

/// <param name="Message">MSG-GEN-10 when no receipt matches.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record PurchaseReceiptSearchResult(
    Paged<PurchaseReceiptRow> Page, PurchaseReceiptTotals Totals, DateOnly From, DateOnly To, string? Message, string? MessageCode);

public sealed record RaftCostReceipt(Guid ReceiptId, string Code, DateOnly ReceiptDate, decimal MaterialCost);

public sealed record RaftMaterialCostRow(RaftRef Raft, ZoneRef Zone, decimal MaterialCost, IReadOnlyList<RaftCostReceipt> Receipts);

public sealed record ExportedFile(string FileName, string ContentType, byte[] Content);

/// <summary>INP-07 – search receipts with totals, raft material cost and Excel export, never wider than the caller's zones.</summary>
public sealed class PurchaseReceiptQueryService(
    IPurchaseReceiptRepository receipts,
    IRaftRepository rafts,
    ZoneAccessGuard zoneAccess,
    ISpreadsheetWriter spreadsheets,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    /// <summary>Longest list, export and cost period, so one request never reads the whole history.</summary>
    public const int MaxPeriodYears = 5;

    public async Task<Result<PurchaseReceiptSearchResult>> SearchAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.View, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // Paging and totals are computed by the database; the page number is capped so the offset cannot overflow.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / 100), Math.Clamp(query.PageSize, 1, 100));
        var (found, totals) = await receipts.PageFiguresAsync(filter.Value!, page, pageSize, cancellationToken);
        // The list always has a period, so an empty one is MSG-GEN-10 (filtered), not MSG-GEN-11.
        var empty = totals.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new PurchaseReceiptSearchResult(
            new Paged<PurchaseReceiptRow>(found.Select(Row).ToList(), page, pageSize, totals.Count),
            Totals(totals),
            filter.Value!.From!.Value, filter.Value.To!.Value,
            empty?.Text,
            empty?.Code);
    }

    /// <summary>Raft material cost per raft with the Completed receipts behind it (filters zone, raft, period, item).</summary>
    public async Task<Result<IReadOnlyList<RaftMaterialCostRow>>> RaftMaterialCostsAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query with { ItemGroup = InputItemGroup.RaftMaterial, StatusId = StatusIds.PurchaseReceipt.Completed }, PermissionAction.View, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var found = (await receipts.SearchAsync(filter.Value!, cancellationToken)).Where(r => r.Raft is not null).ToList();
        return found.GroupBy(r => r.RaftId!.Value).Select(g =>
        {
            var first = g.First();
            var costs = g.Select(r => new RaftCostReceipt(r.Id, r.Code, r.ReceiptDate, PurchaseMoney.RaftMaterialCost(r, query.ItemId))).ToList();
            return new RaftMaterialCostRow(
                new RaftRef(first.Raft!.Id, first.Raft.Code), new ZoneRef(first.Zone.Id, first.Zone.Code, first.Zone.Name), costs.Sum(c => c.MaterialCost), costs);
        }).OrderBy(r => r.Zone.Code).ThenBy(r => r.Raft.Code).ToList();
    }

    /// <summary>The same rows and totals as the list, as an .xlsx file (Export permission, zones limited the same way).</summary>
    public async Task<Result<ExportedFile>> ExportAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.Export, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var rows = (await receipts.ListFiguresAsync(filter.Value!, null, cancellationToken)).Select(Row).ToList();
        var totals = Totals(rows);
        var sheetRows = rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Code, r.ReceiptDate?.ToDateTime(TimeOnly.MinValue), r.Supplier.Name, r.Zone.Code, r.Raft?.Code, r.Status,
            r.Subtotal, r.Discount, r.Payable, r.Returned, r.Paid, r.Outstanding,
        ]).ToList();
        sheetRows.Add(["Total (Completed)", null, null, null, null, $"{totals.CompletedCount} / {totals.Count}",
            totals.Subtotal, totals.Discount, totals.Payable, totals.Returned, totals.Paid, totals.Outstanding]);
        try
        {
            var content = spreadsheets.Write([new SpreadsheetSheet(
                "Purchase receipts",
                ["Code", "Date", "Supplier", "Zone", "Raft", "Status", "Subtotal", "Discount", "Payable", "Returned", "Paid", "Outstanding"],
                sheetRows)]);
            var (from, to) = (filter.Value!.From!.Value, filter.Value.To!.Value);
            return new ExportedFile($"purchase-receipts-{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    /// <summary>Caller zones for the action; a zone or raft filter outside them is 403; the period defaults to this year.</summary>
    private async Task<Result<PurchaseReceiptFilter>> FilterAsync(PurchaseReceiptQuery query, PermissionAction action, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.PurchaseReceipts, action, cancellationToken);
        if (zones.IsEmpty || (query.ZoneId is { } zoneId && !Allows(zones, zoneId)))
        {
            return AppError.Forbidden;
        }

        if (query.RaftId is { } raftId)
        {
            var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
            if (raft is null || !Allows(zones, raft.ZoneId))
            {
                return raft is null ? AppError.Invalid("Raft") : AppError.Forbidden;
            }
        }

        var year = timeProvider.Today().Year;
        var from = query.From ?? (query.To is { } t ? new DateOnly(t.Year, 1, 1) : new DateOnly(year, 1, 1));
        var to = query.To ?? (query.From is { } f ? new DateOnly(f.Year, 12, 31) : new DateOnly(year, 12, 31));
        if (from > to)
        {
            return AppError.Invalid("From date");
        }

        if (to > from.AddYears(MaxPeriodYears))
        {
            return AppError.Invalid("To date", $"The period is at most {MaxPeriodYears} years.");
        }

        return new PurchaseReceiptFilter(zones, query.ItemGroup, query.ItemId, query.ZoneId, query.RaftId, query.SupplierId, query.StatusId, from, to, query.Code);
    }

    private static bool Allows(PermittedZones zones, Guid zoneId) => zones.AllZones || zones.ZoneIds.Contains(zoneId);

    private PurchaseReceiptRow Row(ReceiptFigureRow r) => new(
        r.Id, r.Code, PurchaseMoney.DateOrNull(r.ReceiptDate), new SupplierRef(r.SupplierId, r.SupplierName, r.SupplierPhone),
        new ZoneRef(r.ZoneId, r.ZoneCode, r.ZoneName), r.RaftId is { } raftId ? new RaftRef(raftId, r.RaftCode!) : null,
        r.StatusId, statusCatalog.DisplayNameOf(r.StatusId), r.Subtotal, r.Discount, r.Payable, r.Returned, r.Paid, r.Outstanding);

    private static PurchaseReceiptTotals Totals(ReceiptFigureTotals t) =>
        new(t.Count, t.CompletedCount, t.Subtotal, t.Discount, t.Payable, t.Returned, t.Paid, t.Outstanding);

    private static PurchaseReceiptTotals Totals(IReadOnlyList<PurchaseReceiptRow> rows)
    {
        var completed = rows.Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed).ToList();
        return new PurchaseReceiptTotals(
            rows.Count, completed.Count, completed.Sum(r => r.Subtotal), completed.Sum(r => r.Discount), completed.Sum(r => r.Payable),
            completed.Sum(r => r.Returned), completed.Sum(r => r.Paid), completed.Sum(r => r.Outstanding));
    }
}
