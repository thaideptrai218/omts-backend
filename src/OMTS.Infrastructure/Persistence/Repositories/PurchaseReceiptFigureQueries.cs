using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// PURCHASE-03 / supplier debt read side: money figures are summed by SQL and paged or grouped in the database, so a list,
/// its totals, the export, the raft material cost and the supplier debts never materialize receipts with their lines,
/// returns and payments. The item group, item and raft filters are line filters (PURCHASE-03 step 3).
/// </summary>
internal sealed partial class PurchaseReceiptRepository
{
    public async Task<(IReadOnlyList<ReceiptFigureRow> Items, ReceiptListTotals Totals)> PageFiguresAsync(
        PurchaseReceiptFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var totals = await TotalsAsync(filter, cancellationToken);
        if (totals.Count == 0)
        {
            return ([], totals);
        }

        var items = await Figures(Newest(Filtered(filter, db.PurchaseReceipts.AsNoTracking())).Skip((page - 1) * pageSize).Take(pageSize), filter)
            .ToListAsync(cancellationToken);
        return (items, totals);
    }

    public async Task<IReadOnlyList<ReceiptFigureRow>> ListFiguresAsync(PurchaseReceiptFilter filter, int? take, CancellationToken cancellationToken)
    {
        var query = Newest(Filtered(filter, db.PurchaseReceipts.AsNoTracking()));
        return await Figures(take is { } limit ? query.Take(limit) : query, filter).ToListAsync(cancellationToken);
    }

    public async Task<ReceiptListTotals> TotalsAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken)
    {
        var matching = Filtered(filter, db.PurchaseReceipts.AsNoTracking());
        var count = await matching.CountAsync(cancellationToken);
        var completedCount = await matching.CountAsync(r => r.StatusId == StatusIds.PurchaseReceipt.Completed, cancellationToken);
        var (receipts, returns) = await SumsAsync(filter, cancellationToken);
        decimal? raftMaterialCost = null;
        if (filter.RaftId is not null)
        {
            // BR-FARM-05: the material cost of the raft counts its Material lines only, whatever group is chosen.
            var (material, materialReturns) = filter.ItemGroup == InputItemGroup.Material
                ? (receipts, returns)
                : await SumsAsync(filter with { ItemGroup = InputItemGroup.Material }, cancellationToken);
            raftMaterialCost = material - materialReturns;
        }

        return new ReceiptListTotals(count, completedCount, receipts, returns, raftMaterialCost);
    }

    public async Task<(IReadOnlyList<RaftCostLineRow> Lines, IReadOnlyList<RaftCostReturnRow> Returns)> RaftMaterialCostRowsAsync(
        PurchaseReceiptFilter filter, CancellationToken cancellationToken)
    {
        var (from, to) = (filter.From ?? DateOnly.MinValue, filter.To ?? DateOnly.MaxValue);
        var lines = OfCompletedReceipts(filter, MatchingLines(filter with { ItemGroup = InputItemGroup.Material }, db.PurchaseReceiptLines.AsNoTracking()))
            .Where(l => l.RaftId != null);
        var lineRows = await lines
            .Where(l => l.Receipt.ReceiptDate >= from && l.Receipt.ReceiptDate <= to)
            .Select(l => new RaftCostLineRow(
                l.RaftId!.Value, l.Raft!.Code, l.Raft.ZoneId, l.Raft.Zone.Code, l.Raft.Zone.Name,
                l.ReceiptId, l.Receipt.Code, l.Receipt.ReceiptDate, l.Receipt.SupplierId, l.Receipt.Supplier.Name,
                l.Id, l.LineNumber, l.ItemId!.Value, l.Item!.Code, l.Item.Name, l.Unit, l.Quantity ?? 0m, l.UnitPrice ?? 0m, l.LineAmount))
            .ToListAsync(cancellationToken);
        var lineIds = lines.Select(l => l.Id);
        var returnRows = await db.PurchaseReturnLines.AsNoTracking()
            .Where(rl => lineIds.Contains(rl.ReceiptLineId) && rl.Return.ReturnDate >= from && rl.Return.ReturnDate <= to)
            .Select(rl => new RaftCostReturnRow(
                rl.ReceiptLine.RaftId!.Value, rl.ReceiptLine.Raft!.Code, rl.ReceiptLine.Raft.ZoneId, rl.ReceiptLine.Raft.Zone.Code, rl.ReceiptLine.Raft.Zone.Name,
                rl.ReturnId, rl.Return.Code, rl.Return.ReturnDate, rl.ReceiptLine.ReceiptId, rl.ReceiptLine.Receipt.Code,
                rl.ReceiptLineId, rl.ReceiptLine.LineNumber, rl.ReceiptLine.Item!.Name, rl.ReceiptLine.Unit, rl.Quantity, rl.ReturnValue))
            .ToListAsync(cancellationToken);
        return (lineRows, returnRows);
    }

    public async Task<IReadOnlyList<ReceiptFigureRow>> CompletedFiguresOfSupplierAsync(Guid supplierId, CancellationToken cancellationToken) =>
        await Figures(db.PurchaseReceipts.AsNoTracking().Where(r => r.SupplierId == supplierId && r.StatusId == StatusIds.PurchaseReceipt.Completed)
                .OrderBy(r => r.ReceiptDate).ThenBy(r => r.Code), null)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SupplierDebtFigures>> SupplierDebtFiguresAsync(IReadOnlyCollection<Guid>? supplierIds, CancellationToken cancellationToken)
    {
        // One grouped query per kind of figure, merged per supplier: nothing but the sums leaves the database.
        var completed = db.PurchaseReceipts.AsNoTracking()
            .Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed && (supplierIds == null || supplierIds.Contains(r.SupplierId)));
        var ids = completed.Select(r => r.Id);
        var receipts = await completed.GroupBy(r => r.SupplierId)
            .Select(g => new { g.Key, Name = g.Max(r => r.Supplier.Name) })
            .ToListAsync(cancellationToken);
        var totals = await db.PurchaseReceiptLines.Where(l => ids.Contains(l.ReceiptId))
            .GroupBy(l => l.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(l => l.LineAmount) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var returned = await db.PurchaseReturnLines.Where(l => ids.Contains(l.Return.ReceiptId))
            .GroupBy(l => l.Return.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(l => l.ReturnValue) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var paid = await RecordedPayments(ids)
            .GroupBy(p => p.Receipt!.SupplierId).Select(g => new { g.Key, Amount = g.Sum(p => p.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var refunded = await db.PurchaseReturns.Where(r => ids.Contains(r.ReceiptId))
            .GroupBy(r => r.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(r => r.RefundAmount) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        return receipts.Select(r => new SupplierDebtFigures(
            r.Key, r.Name ?? string.Empty, totals.GetValueOrDefault(r.Key), returned.GetValueOrDefault(r.Key),
            paid.GetValueOrDefault(r.Key), refunded.GetValueOrDefault(r.Key))).ToList();
    }

    /// <summary>
    /// Σ of the matching lines of the matching Completed receipts received in the period, and Σ of the returns of those
    /// lines dated in the period whatever the date received (PURCHASE-03 step 3, BR-FARM-05).
    /// </summary>
    private async Task<(decimal Receipts, decimal Returns)> SumsAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken)
    {
        if (filter.StatusId is { } statusId && statusId != StatusIds.PurchaseReceipt.Completed)
        {
            // Totals count Completed receipts only (BR-PURCHASE-03), and only Completed receipts have returns.
            return (0, 0);
        }

        var (from, to) = (filter.From ?? DateOnly.MinValue, filter.To ?? DateOnly.MaxValue);
        var lines = OfCompletedReceipts(filter, MatchingLines(filter, db.PurchaseReceiptLines.AsNoTracking()));
        var receipts = await lines.Where(l => l.Receipt.ReceiptDate >= from && l.Receipt.ReceiptDate <= to)
            .SumAsync(l => (decimal?)l.LineAmount, cancellationToken) ?? 0m;
        var lineIds = lines.Select(l => l.Id);
        var returns = await db.PurchaseReturnLines.AsNoTracking()
            .Where(rl => lineIds.Contains(rl.ReceiptLineId) && rl.Return.ReturnDate >= from && rl.Return.ReturnDate <= to)
            .SumAsync(rl => (decimal?)rl.ReturnValue, cancellationToken) ?? 0m;
        return (receipts, returns);
    }

    private IQueryable<SupplierPayment> RecordedPayments(IQueryable<Guid> receiptIds) =>
        db.SupplierPayments.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded && p.ReceiptId != null && receiptIds.Contains(p.ReceiptId.Value));

    private static IOrderedQueryable<PurchaseReceipt> Newest(IQueryable<PurchaseReceipt> rows) =>
        rows.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Code);

    /// <summary>The receipt with its total, the total of the lines matching the filter, returns, recorded payments and refunds as SQL subqueries.</summary>
    private static IQueryable<ReceiptFigureRow> Figures(IQueryable<PurchaseReceipt> receipts, PurchaseReceiptFilter? filter)
    {
        var (group, itemId, raftId) = (filter?.ItemGroup, filter?.ItemId, filter?.RaftId);
        return receipts.Select(r => new ReceiptFigureRow(
            r.Id, r.Code, r.ReceiptDate, r.SupplierId, r.Supplier.Name, r.Supplier.PhoneNumber, r.StatusId, r.RecordedByAccountId,
            r.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m,
            r.Lines.Where(l => (group == null || (l.Item != null && l.Item.Group == group)) && (itemId == null || l.ItemId == itemId) && (raftId == null || l.RaftId == raftId))
                .Sum(l => (decimal?)l.LineAmount) ?? 0m,
            r.Returns.SelectMany(x => x.Lines).Sum(l => (decimal?)l.ReturnValue) ?? 0m,
            r.Payments.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded).Sum(p => (decimal?)p.Amount) ?? 0m,
            r.Returns.Sum(x => (decimal?)x.RefundAmount) ?? 0m));
    }

    private static IQueryable<PurchaseReceiptLine> MatchingLines(PurchaseReceiptFilter filter, IQueryable<PurchaseReceiptLine> lines)
    {
        if (filter.ItemGroup is { } group)
        {
            lines = lines.Where(l => l.Item != null && l.Item.Group == group);
        }

        if (filter.ItemId is { } itemId)
        {
            lines = lines.Where(l => l.ItemId == itemId);
        }

        if (filter.RaftId is { } raftId)
        {
            lines = lines.Where(l => l.RaftId == raftId);
        }

        return lines;
    }

    /// <summary>Lines of Completed receipts that match the supplier and code filters (not the period).</summary>
    private static IQueryable<PurchaseReceiptLine> OfCompletedReceipts(PurchaseReceiptFilter filter, IQueryable<PurchaseReceiptLine> lines)
    {
        lines = lines.Where(l => l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed);
        if (filter.SupplierId is { } supplierId)
        {
            lines = lines.Where(l => l.Receipt.SupplierId == supplierId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            var code = filter.Code.Trim();
            lines = lines.Where(l => l.Receipt.Code.Contains(code));
        }

        return lines;
    }

    private static IQueryable<PurchaseReceipt> Filtered(PurchaseReceiptFilter filter, IQueryable<PurchaseReceipt> query)
    {
        if (filter.ItemGroup is not null || filter.ItemId is not null || filter.RaftId is not null)
        {
            var (group, itemId, raftId) = (filter.ItemGroup, filter.ItemId, filter.RaftId);
            query = query.Where(r => r.Lines.Any(l =>
                (group == null || (l.Item != null && l.Item.Group == group)) && (itemId == null || l.ItemId == itemId) && (raftId == null || l.RaftId == raftId)));
        }

        if (filter.SupplierId is { } supplierId)
        {
            query = query.Where(r => r.SupplierId == supplierId);
        }

        if (filter.StatusId is { } statusId)
        {
            query = query.Where(r => r.StatusId == statusId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(r => r.ReceiptDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.ReceiptDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            var code = filter.Code.Trim();
            query = query.Where(r => r.Code.Contains(code));
        }

        return query;
    }
}
