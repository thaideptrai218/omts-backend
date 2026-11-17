using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SalesReturnRepository(OmtsDbContext db) : ISalesReturnRepository
{
    public void Add(SalesReturn salesReturn) => db.SalesReturns.Add(salesReturn);

    public Task<SalesReturn?> GetForEditAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.SalesReturns.AsSplitQuery()
            .Include(r => r.Lines).ThenInclude(l => l.HarvestLot).ThenInclude(l => l.Raft)
            .Include(r => r.Lines).ThenInclude(l => l.HarvestLot).ThenInclude(l => l.ProductLines)
            .Include(r => r.Invoice).ThenInclude(i => i.Customer)
            .FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);

    public Task<SalesReturn?> GetDetailAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.SalesReturns.AsNoTracking().AsSplitQuery()
            .Include(r => r.Lines).ThenInclude(l => l.HarvestLot)
            .Include(r => r.Invoice).ThenInclude(i => i.Lines).ThenInclude(l => l.Product)
            .Include(r => r.Invoice).ThenInclude(i => i.Customer)
            .Include(r => r.Invoice).ThenInclude(i => i.Zone)
            .Include(r => r.Invoice).ThenInclude(i => i.SalesOrder)
            .FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);

    public async Task<(IReadOnlyList<SalesReturnListRow> Rows, int Total, SalesReturnSums Totals)> SearchAsync(
        SalesReturnFilter filter, SalesReturnSort sort, bool descending, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.SalesReturns.AsNoTracking().AsQueryable();
        if (filter.From is { } from)
        {
            query = query.Where(r => r.ReturnDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.ReturnDate <= to);
        }

        if (filter.ZoneId is { } zoneId)
        {
            query = query.Where(r => r.Invoice.ZoneId == zoneId);
        }

        if (filter.ZoneIds is { } zoneIds)
        {
            var ids = zoneIds.ToList();
            query = query.Where(r => ids.Contains(r.Invoice.ZoneId));
        }

        if (filter.CustomerId is { } customerId)
        {
            query = query.Where(r => r.Invoice.CustomerId == customerId);
        }

        if (filter.StatusId is { } statusId)
        {
            query = query.Where(r => r.StatusId == statusId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            query = query.Where(r => r.Code.Contains(text) || r.Invoice.Code.Contains(text)
                || (r.Invoice.Customer != null && (r.Invoice.Customer.Code.Contains(text) || r.Invoice.Customer.Name.Contains(text))));
        }

        var total = await query.CountAsync(cancellationToken);
        var standing = query.Where(SalesReturn.Standing);
        var totals = new SalesReturnSums(
            await standing.SumAsync(r => (decimal?)r.Value, cancellationToken) ?? 0m,
            await standing.SelectMany(r => r.Lines).Where(l => l.Outcome == SalesReturnOutcome.BackForSale).SumAsync(l => (decimal?)l.ReturnedKg, cancellationToken) ?? 0m,
            await standing.SelectMany(r => r.Lines).SumAsync(l => (decimal?)l.DiscardedKg, cancellationToken) ?? 0m);

        var ordered = (sort, descending) switch
        {
            (SalesReturnSort.ReturnDate, true) => query.OrderByDescending(r => r.ReturnDate).ThenByDescending(r => r.Code),
            (SalesReturnSort.ReturnDate, false) => query.OrderBy(r => r.ReturnDate).ThenBy(r => r.Code),
            (SalesReturnSort.Value, true) => query.OrderByDescending(r => r.Value).ThenByDescending(r => r.Code),
            (SalesReturnSort.Value, false) => query.OrderBy(r => r.Value).ThenBy(r => r.Code),
            (_, true) => query.OrderByDescending(r => r.Code),
            _ => query.OrderBy(r => r.Code),
        };
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new SalesReturnListRow(
                r.Id, r.Code, r.ReturnDate, r.CreatedAt, r.InvoiceId, r.Invoice.Code, r.Invoice.SalesOrderId, r.Invoice.SalesOrder == null ? null : r.Invoice.SalesOrder.Code,
                r.Invoice.CustomerId, r.Invoice.Customer == null ? null : r.Invoice.Customer.Code, r.Invoice.Customer == null ? null : r.Invoice.Customer.Name, r.Invoice.ZoneId,
                r.Invoice.Zone.Code, r.Value, r.StatusId, r.RecordedByAccountId, r.Lines.Count(),
                r.Lines.Where(l => l.Outcome == SalesReturnOutcome.BackForSale).Sum(l => (decimal?)l.ReturnedKg) ?? 0m, r.Lines.Sum(l => (decimal?)l.DiscardedKg) ?? 0m))
            .ToListAsync(cancellationToken);
        return (rows, total, totals);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SalesReturnOfOrder>>> OfOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<SalesReturnOfOrder>>();
        }

        var rows = await db.SalesReturns.AsNoTracking().Where(r => r.Invoice.SalesOrderId != null && ids.Contains(r.Invoice.SalesOrderId.Value))
            .Select(r => new { OrderId = r.Invoice.SalesOrderId!.Value, r.Id, r.Code, r.StatusId }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.OrderId).ToDictionary(
            g => g.Key, g => (IReadOnlyList<SalesReturnOfOrder>)g.OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => new SalesReturnOfOrder(r.Id, r.Code, r.StatusId)).ToList());
    }
}
