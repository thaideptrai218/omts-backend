using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SalesOrderRepository(OmtsDbContext db) : ISalesOrderRepository
{
    public Task<SalesOrder?> GetForEditAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.SalesOrders
            .Include(o => o.Customer)
            .Include(o => o.PurchaseRequest)
            .Include(o => o.Lines).ThenInclude(l => l.Product)
            .AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public Task<SalesOrder?> FindByCodeAsync(string code, Guid? exceptOrderId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Code.ToUpper() == upper && o.Id != exceptOrderId, cancellationToken);
    }

    public Task<SalesOrder?> FindByPurchaseRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.PurchaseRequestId == requestId, cancellationToken);

    public async Task<IReadOnlyList<SalesOrder>> FindByPurchaseRequestsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken)
    {
        var ids = requestIds.Distinct().ToList();
        return ids.Count == 0 ? [] : await db.SalesOrders.AsNoTracking().Where(o => o.PurchaseRequestId != null && ids.Contains(o.PurchaseRequestId.Value)).ToListAsync(cancellationToken);
    }

    public Task<SalesOrder?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.DeduplicationKey == key, cancellationToken);

    public async Task<(IReadOnlyList<SalesOrderRow> Rows, int Total, SalesOrderTotals Totals)> SearchAsync(
        SalesOrderFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var ids = await Sorted(query, filter).Skip((page - 1) * pageSize).Take(pageSize).Select(f => f.Id).ToListAsync(cancellationToken);
        // The sums leave the cancelled orders out; the figures of each match are read and added up here (a decimal sum of whole VND).
        var live = total == 0 ? [] : await query.Where(f => f.StatusId != StatusIds.SalesOrder.Cancelled).Select(f => new { f.Total, f.CustomerPaid, f.Invoiced }).ToListAsync(cancellationToken);
        var totals = new SalesOrderTotals(live.Sum(f => f.Total), live.Sum(f => f.Invoiced), live.Sum(f => f.CustomerPaid));
        return (await RowsAsync(ids, cancellationToken), total, totals);
    }

    public async Task<IReadOnlyList<SalesOrderRow>> ListAsync(SalesOrderFilter filter, CancellationToken cancellationToken) =>
        await RowsAsync(await Sorted(Filtered(filter), filter).Select(f => f.Id).ToListAsync(cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<CustomerPayment>> AdvancesOfAsync(Guid orderId, CancellationToken cancellationToken) =>
        await AdvancesOfAsync([orderId], cancellationToken);

    public async Task<IReadOnlyList<CustomerPayment>> AdvancesOfAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var ids = orderIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.CustomerPayments.AsNoTracking()
                .Include(p => p.Allocations)
                .Where(p => p.SalesOrderId != null && ids.Contains(p.SalesOrderId.Value))
                .OrderBy(p => p.PaidAt).ThenBy(p => p.Code)
                .ToListAsync(cancellationToken);
    }

    public Task<bool> HasLiveInvoiceAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.SalesInvoices.AsNoTracking().AnyAsync(i => i.SalesOrderId == orderId && i.StatusId != StatusIds.SalesInvoice.Cancelled, cancellationToken);

    public async Task<decimal> InvoicedTotalAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.SalesInvoices.AsNoTracking().Where(i => i.SalesOrderId == orderId && i.StatusId != StatusIds.SalesInvoice.Cancelled).SumAsync(i => (decimal?)i.Total, cancellationToken) ?? 0m;

    public async Task<IReadOnlyList<SalesOrderInvoiceRow>> InvoicesOfAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.SalesInvoices.AsNoTracking().Where(i => i.SalesOrderId == orderId).OrderBy(i => i.InvoicedAt).ThenBy(i => i.Code)
            .Select(i => new SalesOrderInvoiceRow(i.Id, i.Code, i.InvoicedAt, i.Total, i.DiscountAmount, i.StatusId, i.CreatedByAccountId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, decimal>> DeliveredByLineAsync(IReadOnlyCollection<Guid> orderIds, Guid? exceptInvoiceId, CancellationToken cancellationToken)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var rows = await db.SalesInvoiceLines.AsNoTracking()
            .Where(l => l.SalesOrderLineId != null && l.Invoice.SalesOrderId != null && ids.Contains(l.Invoice.SalesOrderId.Value)
                && l.Invoice.StatusId != StatusIds.SalesInvoice.Cancelled && l.InvoiceId != exceptInvoiceId)
            .Select(l => new { Line = l.SalesOrderLineId!.Value, l.Quantity })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.Line).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
    }

    public async Task<IReadOnlyList<CustomerPayment>> CountedAdvancesForUpdateAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.CustomerPayments.Include(p => p.Allocations)
            .Where(p => p.SalesOrderId == orderId && p.Kind == CustomerPaymentKind.Payment
                && (p.StatusId == StatusIds.CustomerPayment.Completed || p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation))
            .OrderBy(p => p.PaidAt).ThenBy(p => p.Code)
            .ToListAsync(cancellationToken);

    public void Add(SalesOrder order) => db.SalesOrders.Add(order);

    public void AddLine(SalesOrderLine line) => db.SalesOrderLines.Add(line);

    public void RemoveLine(SalesOrderLine line) => db.SalesOrderLines.Remove(line);

    private sealed class Figures
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public DateTimeOffset OrderedAt { get; init; }
        public DateTimeOffset? PickupAt { get; init; }
        public int StatusId { get; init; }
        public decimal Total { get; init; }
        public decimal CustomerPaid { get; init; }
        public decimal Invoiced { get; init; }
    }

    private async Task<IReadOnlyList<SalesOrderRow>> RowsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var orders = (await db.SalesOrders.AsNoTracking()
            .Include(o => o.Customer).Include(o => o.Lines)
            .Where(o => ids.Contains(o.Id)).AsSplitQuery().ToListAsync(cancellationToken)).ToDictionary(o => o.Id);
        var paid = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.SalesOrderId != null && ids.Contains(p.SalesOrderId.Value) && p.Kind == CustomerPaymentKind.Payment
                && (p.StatusId == StatusIds.CustomerPayment.Completed || p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation))
            .GroupBy(p => p.SalesOrderId!.Value).Select(g => new { g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(cancellationToken);
        var invoices = (await db.SalesInvoices.AsNoTracking()
            .Where(i => i.SalesOrderId != null && ids.Contains(i.SalesOrderId.Value) && i.StatusId != StatusIds.SalesInvoice.Cancelled)
            .OrderBy(i => i.InvoicedAt).ThenBy(i => i.Code)
            .Select(i => new { OrderId = i.SalesOrderId!.Value, i.Code, i.Total }).ToListAsync(cancellationToken)).ToLookup(i => i.OrderId);
        return ids.Select(id => new SalesOrderRow(
            orders[id], invoices[id].Sum(i => i.Total), paid.FirstOrDefault(p => p.Key == id)?.Sum ?? 0m, invoices[id].Select(i => i.Code).ToList())).ToList();
    }

    private IQueryable<Figures> Filtered(SalesOrderFilter filter)
    {
        var orders = db.SalesOrders.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            orders = orders.Where(o => o.Code.Contains(text)
                || (o.Customer != null && (o.Customer.Code.Contains(text) || o.Customer.Name.Contains(text) || (o.Customer.PhoneNumber != null && o.Customer.PhoneNumber.Contains(text)))));
        }

        if (filter.StatusIds is { Count: > 0 } statusIds)
        {
            orders = orders.Where(o => statusIds.Contains(o.StatusId));
        }

        if (filter.From is { } from)
        {
            orders = orders.Where(o => o.OrderDate >= from);
        }

        if (filter.To is { } to)
        {
            orders = orders.Where(o => o.OrderDate <= to);
        }

        if (filter.PickupFrom is { } pickupFrom)
        {
            orders = orders.Where(o => o.PickupDate != null && o.PickupDate >= pickupFrom);
        }

        if (filter.PickupTo is { } pickupTo)
        {
            orders = orders.Where(o => o.PickupDate != null && o.PickupDate <= pickupTo);
        }

        if (filter.CustomerId is { } customerId)
        {
            orders = orders.Where(o => o.CustomerId == customerId);
        }

        if (filter.Source is { } source)
        {
            orders = orders.Where(o => o.Source == source);
        }

        if (filter.CreatedById is { } createdBy)
        {
            orders = orders.Where(o => o.CreatedByAccountId == createdBy);
        }

        if (filter.ReceivedById is { } receivedBy)
        {
            orders = orders.Where(o => o.ReceivedByAccountId == receivedBy);
        }

        return orders.Select(o => new Figures
        {
            Id = o.Id, Code = o.Code, OrderedAt = o.OrderedAt, PickupAt = o.PickupAt, StatusId = o.StatusId, Total = o.Total,
            CustomerPaid = db.CustomerPayments
                .Where(p => p.SalesOrderId == o.Id && p.Kind == CustomerPaymentKind.Payment
                    && (p.StatusId == StatusIds.CustomerPayment.Completed || p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation))
                .Sum(p => (decimal?)p.Amount) ?? 0m,
            Invoiced = db.SalesInvoices.Where(i => i.SalesOrderId == o.Id && i.StatusId != StatusIds.SalesInvoice.Cancelled).Sum(i => (decimal?)i.Total) ?? 0m,
        });
    }

    private static IQueryable<Figures> Sorted(IQueryable<Figures> query, SalesOrderFilter filter)
    {
        var descending = filter.Descending;
        IOrderedQueryable<Figures> Order<TKey>(Expression<Func<Figures, TKey>> key) => descending ? query.OrderByDescending(key) : query.OrderBy(key);
        var ordered = filter.Sort switch
        {
            SalesOrderSort.OrderedAt => Order(f => f.OrderedAt),
            SalesOrderSort.PickupAt => Order(f => f.PickupAt),
            SalesOrderSort.Total => Order(f => f.Total),
            _ => Order(f => f.Code),
        };
        return ordered.ThenBy(f => f.Id);
    }
}
