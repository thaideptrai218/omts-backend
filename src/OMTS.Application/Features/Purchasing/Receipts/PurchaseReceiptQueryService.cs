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

public sealed record PurchaseReceiptSearchResult(Paged<PurchaseReceiptRow> Page, PurchaseReceiptTotals Totals, DateOnly From, DateOnly To, string? Message);

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
    public async Task<Result<PurchaseReceiptSearchResult>> SearchAsync(PurchaseReceiptQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.View, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var found = await receipts.SearchAsync(filter.Value!, cancellationToken);
        var (page, pageSize) = (Math.Max(query.Page, 1), Math.Clamp(query.PageSize, 1, 100));
        var rows = found.Select(Row).ToList();
        return new PurchaseReceiptSearchResult(
            new Paged<PurchaseReceiptRow>(rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, rows.Count),
            Totals(rows),
            filter.Value!.From!.Value, filter.Value.To!.Value,
            rows.Count == 0 ? UserMessages.NoData("purchase receipts").Text : null);
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

        var rows = (await receipts.SearchAsync(filter.Value!, cancellationToken)).Select(Row).ToList();
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

        return new PurchaseReceiptFilter(zones, query.ItemGroup, query.ItemId, query.ZoneId, query.RaftId, query.SupplierId, query.StatusId, from, to, query.Code);
    }

    private static bool Allows(PermittedZones zones, Guid zoneId) => zones.AllZones || zones.ZoneIds.Contains(zoneId);

    private PurchaseReceiptRow Row(PurchaseReceipt r)
    {
        var f = PurchaseMoney.Of(r);
        return new PurchaseReceiptRow(
            r.Id, r.Code, PurchaseMoney.DateOrNull(r.ReceiptDate), new SupplierRef(r.Supplier.Id, r.Supplier.Name, r.Supplier.PhoneNumber),
            new ZoneRef(r.Zone.Id, r.Zone.Code, r.Zone.Name), r.Raft is null ? null : new RaftRef(r.Raft.Id, r.Raft.Code),
            r.StatusId, statusCatalog.DisplayNameOf(r.StatusId), f.Subtotal, f.Discount, f.Payable, f.Returned, f.Paid, f.Outstanding);
    }

    private static PurchaseReceiptTotals Totals(IReadOnlyList<PurchaseReceiptRow> rows)
    {
        var completed = rows.Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed).ToList();
        return new PurchaseReceiptTotals(
            rows.Count, completed.Count, completed.Sum(r => r.Subtotal), completed.Sum(r => r.Discount), completed.Sum(r => r.Payable),
            completed.Sum(r => r.Returned), completed.Sum(r => r.Paid), completed.Sum(r => r.Outstanding));
    }
}
