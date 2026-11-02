using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Repositories.Purchasing;

/// <summary>
/// INP-07 / INP-06 read side: money figures are summed by SQL subqueries and paged or grouped in the database, so a list,
/// its totals, the export and the supplier debts never materialize receipts with their lines, returns and payments.
/// </summary>
internal sealed partial class PurchaseReceiptRepository
{
    public async Task<(IReadOnlyList<ReceiptFigureRow> Items, ReceiptFigureTotals Totals)> PageFiguresAsync(
        PurchaseReceiptFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matching = Filtered(filter, db.PurchaseReceipts.AsNoTracking());
        var count = await matching.CountAsync(cancellationToken);
        if (count == 0)
        {
            return ([], ReceiptFigureTotals.Empty);
        }

        // Money totals count the Completed receipts only; each sum is one query over the child table.
        var completed = matching.Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed);
        var ids = completed.Select(r => r.Id);
        var totals = new ReceiptFigureTotals(
            count,
            await completed.CountAsync(cancellationToken),
            await db.PurchaseReceiptLines.Where(l => ids.Contains(l.ReceiptId)).SumAsync(l => (decimal?)l.LineAmount, cancellationToken) ?? 0m,
            await completed.SumAsync(r => (decimal?)r.DiscountAmount, cancellationToken) ?? 0m,
            await db.PurchaseReturnLines.Where(l => ids.Contains(l.Return.ReceiptId)).SumAsync(l => (decimal?)l.ReturnValue, cancellationToken) ?? 0m,
            await RecordedPayments(ids).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m,
            await db.PurchaseReturns.Where(r => ids.Contains(r.ReceiptId)).SumAsync(r => (decimal?)r.RefundAmount, cancellationToken) ?? 0m);
        var items = await Figures(Newest(matching).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(cancellationToken);
        return (items, totals);
    }

    public async Task<IReadOnlyList<ReceiptFigureRow>> ListFiguresAsync(PurchaseReceiptFilter filter, int? take, CancellationToken cancellationToken)
    {
        var query = Newest(Filtered(filter, db.PurchaseReceipts.AsNoTracking()));
        return await Figures(take is { } limit ? query.Take(limit) : query).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReceiptFigureRow>> CompletedFiguresOfSupplierAsync(Guid supplierId, CancellationToken cancellationToken) =>
        await Figures(db.PurchaseReceipts.AsNoTracking().Where(r => r.SupplierId == supplierId && r.StatusId == StatusIds.PurchaseReceipt.Completed)
                .OrderBy(r => r.ReceiptDate).ThenBy(r => r.Code))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SupplierDebtFigures>> SupplierDebtFiguresAsync(IReadOnlyCollection<Guid>? supplierIds, CancellationToken cancellationToken)
    {
        // One grouped query per kind of figure, merged per supplier: nothing but the sums leaves the database.
        var completed = db.PurchaseReceipts.AsNoTracking()
            .Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed && (supplierIds == null || supplierIds.Contains(r.SupplierId)));
        var ids = completed.Select(r => r.Id);
        var receipts = await completed.GroupBy(r => r.SupplierId)
            .Select(g => new { g.Key, Name = g.Max(r => r.Supplier.Name), Discount = g.Sum(r => r.DiscountAmount) })
            .ToListAsync(cancellationToken);
        var subtotals = await db.PurchaseReceiptLines.Where(l => ids.Contains(l.ReceiptId))
            .GroupBy(l => l.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(l => l.LineAmount) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var returned = await db.PurchaseReturnLines.Where(l => ids.Contains(l.Return.ReceiptId))
            .GroupBy(l => l.Return.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(l => l.ReturnValue) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var paid = await RecordedPayments(ids)
            .GroupBy(p => p.Receipt!.SupplierId).Select(g => new { g.Key, Amount = g.Sum(p => p.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var refunded = await db.PurchaseReturns.Where(r => ids.Contains(r.ReceiptId))
            .GroupBy(r => r.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(r => r.RefundAmount) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        return receipts.Select(r => new SupplierDebtFigures(
            r.Key, r.Name ?? string.Empty, subtotals.GetValueOrDefault(r.Key) - r.Discount, returned.GetValueOrDefault(r.Key),
            paid.GetValueOrDefault(r.Key), refunded.GetValueOrDefault(r.Key))).ToList();
    }

    private IQueryable<SupplierPayment> RecordedPayments(IQueryable<Guid> receiptIds) =>
        db.SupplierPayments.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded && p.ReceiptId != null && receiptIds.Contains(p.ReceiptId.Value));

    private static IOrderedQueryable<PurchaseReceipt> Newest(IQueryable<PurchaseReceipt> rows) =>
        rows.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Code);

    /// <summary>The receipt with its subtotal, returned value, recorded payments and refunds as SQL subqueries.</summary>
    private static IQueryable<ReceiptFigureRow> Figures(IQueryable<PurchaseReceipt> receipts) => receipts.Select(r => new ReceiptFigureRow(
        r.Id, r.Code, r.ReceiptDate, r.SupplierId, r.Supplier.Name, r.Supplier.PhoneNumber, r.ZoneId, r.Zone.Code, r.Zone.Name,
        r.RaftId, r.Raft == null ? null : r.Raft.Code, r.StatusId,
        r.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m,
        r.DiscountAmount,
        r.Returns.SelectMany(x => x.Lines).Sum(l => (decimal?)l.ReturnValue) ?? 0m,
        r.Payments.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded).Sum(p => (decimal?)p.Amount) ?? 0m,
        r.Returns.Sum(x => (decimal?)x.RefundAmount) ?? 0m));

    private static IQueryable<PurchaseReceipt> Filtered(PurchaseReceiptFilter filter, IQueryable<PurchaseReceipt> query)
    {
        if (!filter.Zones.AllZones)
        {
            query = query.Where(r => filter.Zones.ZoneIds.Contains(r.ZoneId));
        }

        if (filter.ItemGroup is { } group)
        {
            query = query.Where(r => r.Lines.Any(l => l.Item.Group == group));
        }

        if (filter.ItemId is { } itemId)
        {
            query = query.Where(r => r.Lines.Any(l => l.ItemId == itemId));
        }

        if (filter.ZoneId is { } zoneId)
        {
            query = query.Where(r => r.ZoneId == zoneId);
        }

        if (filter.RaftId is { } raftId)
        {
            query = query.Where(r => r.RaftId == raftId);
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
            // Drafts without a date yet are always listed.
            query = query.Where(r => r.ReceiptDate >= from || r.ReceiptDate == DateOnly.MinValue);
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
