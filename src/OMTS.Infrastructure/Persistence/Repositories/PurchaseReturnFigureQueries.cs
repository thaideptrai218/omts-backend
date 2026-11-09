using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>The purchase return list: filter, sort, paging and sums run in the database, on the return date.</summary>
internal sealed partial class PurchaseReceiptRepository
{
    public async Task<(IReadOnlyList<PurchaseReturnFigureRow> Items, int Total, decimal TotalValue)> SearchReturnsAsync(
        PurchaseReturnFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matching = FilteredReturns(filter);
        var total = await matching.CountAsync(cancellationToken);
        var recorded = matching.Where(r => r.StatusId == StatusIds.PurchaseReturn.Recorded);
        var value = await recorded.SelectMany(r => r.Lines).SumAsync(l => (decimal?)l.ReturnValue, cancellationToken) ?? 0m;
        var discount = await recorded.SumAsync(r => (decimal?)r.Discount, cancellationToken) ?? 0m;
        var items = await ReturnFigures(SortedReturns(matching, filter).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(cancellationToken);
        return (items, total, value - discount);
    }

    public async Task<IReadOnlyList<PurchaseReturnFigureRow>> ListReturnsAsync(PurchaseReturnFilter filter, CancellationToken cancellationToken) =>
        await ReturnFigures(SortedReturns(FilteredReturns(filter), filter)).ToListAsync(cancellationToken);

    private IQueryable<PurchaseReturn> FilteredReturns(PurchaseReturnFilter filter)
    {
        var (from, to) = (filter.From, filter.To);
        var query = db.PurchaseReturns.AsNoTracking().Where(r => r.ReturnDate >= from && r.ReturnDate <= to);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            query = query.Where(r => r.Code.Contains(text) || (r.Receipt != null && r.Receipt.Code.Contains(text)) || r.Supplier.Name.Contains(text));
        }

        if (filter.FromReceipt is { } fromReceipt)
        {
            query = fromReceipt ? query.Where(r => r.ReceiptId != null) : query.Where(r => r.ReceiptId == null);
        }

        if (filter.SupplierId is { } supplierId)
        {
            query = query.Where(r => r.SupplierId == supplierId);
        }

        if (filter.StatusIds is { } statusIds)
        {
            query = query.Where(r => statusIds.Contains(r.StatusId));
        }

        if (filter.ZoneIds is { } zoneIds)
        {
            query = query.Where(r => r.ZoneId != null && zoneIds.Contains(r.ZoneId.Value));
        }

        return query;
    }

    private static IQueryable<PurchaseReturn> SortedReturns(IQueryable<PurchaseReturn> query, PurchaseReturnFilter filter)
    {
        var descending = filter.Descending;
        var sorted = filter.Sort switch
        {
            PurchaseReturnSort.Code => descending ? query.OrderByDescending(r => r.Code) : query.OrderBy(r => r.Code),
            PurchaseReturnSort.Supplier => descending ? query.OrderByDescending(r => r.Supplier.Name) : query.OrderBy(r => r.Supplier.Name),
            PurchaseReturnSort.Total => descending
                ? query.OrderByDescending(r => (r.Lines.Sum(l => (decimal?)l.ReturnValue) ?? 0m) - r.Discount)
                : query.OrderBy(r => (r.Lines.Sum(l => (decimal?)l.ReturnValue) ?? 0m) - r.Discount),
            PurchaseReturnSort.Status => descending ? query.OrderByDescending(r => r.StatusId) : query.OrderBy(r => r.StatusId),
            _ => descending ? query.OrderByDescending(r => r.ReturnedAt) : query.OrderBy(r => r.ReturnedAt),
        };
        return sorted.ThenByDescending(r => r.Code);
    }

    private static IQueryable<PurchaseReturnFigureRow> ReturnFigures(IQueryable<PurchaseReturn> returns) =>
        returns.Select(r => new PurchaseReturnFigureRow(
            r.Id, r.Code, r.ReturnDate, r.ReceiptId, r.Receipt!.Code, r.SupplierId, r.Supplier.Code, r.Supplier.Name, r.Supplier.PhoneNumber,
            r.ZoneId, r.Zone!.Code, r.Zone!.Name, r.Reason, r.StatusId, r.RecordedByAccountId,
            r.Lines.Sum(l => (decimal?)l.ReturnValue) ?? 0m, r.Discount, r.Lines.Count,
            r.Refunds.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded && p.Kind == SupplierPaymentKind.PaidBack).Sum(p => (decimal?)p.Amount) ?? 0m,
            r.CancelledAt, r.CancelReason, r.CreatedAt, r.UpdatedAt, r.RowVersion, r.ReturnedAt));
}
