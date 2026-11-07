using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// What HARVEST reads of the sales (BR-HARVEST-07, BR-HARVEST-05): the kg on lines of invoices that are not cancelled, at their latest kg. Sales returns come with CLAIM-03, so the returned,
/// back-for-sale and discarded kg are 0 until then. A lot on any invoice, a cancelled one included (it keeps its lines), can no longer be removed.
/// </summary>
internal sealed class HarvestLotSalesQuery(OmtsDbContext db) : IHarvestLotSalesQuery
{
    public async Task<IReadOnlyDictionary<Guid, LotSalesFigures>> FiguresAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        var invoiced = ids.Count == 0
            ? []
            : await db.SalesInvoiceLines.AsNoTracking()
                .Where(l => ids.Contains(l.HarvestLotId) && l.Invoice.StatusId != StatusIds.SalesInvoice.Cancelled)
                .GroupBy(l => l.HarvestLotId)
                .Select(g => new { LotId = g.Key, Kg = g.Sum(l => l.Kg) })
                .ToDictionaryAsync(x => x.LotId, x => x.Kg, cancellationToken);
        // CLAIM-03 seam: the returned kg (and their back-for-sale / discarded split) of the non-cancelled sales return lines go in here. HarvestLotFigures subtracts them from the
        // invoiced kg, so the lot views, the GFR-05 leftover and the HARVEST-04 yield (AC-09: 600 invoiced - 300 returned = 300 sold) need no change.
        return ids.ToDictionary(id => id, id => invoiced.TryGetValue(id, out var kg) ? LotSalesFigures.None with { InvoicedKg = kg } : LotSalesFigures.None);
    }

    public Task<bool> IsOnAnyInvoiceAsync(Guid lotId, CancellationToken cancellationToken) =>
        db.SalesInvoiceLines.AsNoTracking().AnyAsync(l => l.HarvestLotId == lotId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> LotsOnAnyInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        return ids.Count == 0
            ? new HashSet<Guid>()
            : (await db.SalesInvoiceLines.AsNoTracking().Where(l => ids.Contains(l.HarvestLotId)).Select(l => l.HarvestLotId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
    }
}

/// <summary>BR-SALE-01: a request is Fulfilled while a Completed invoice linked to it takes it in full; a cancelled invoice gives it back to Accepted.</summary>
internal sealed class PurchaseRequestFulfilmentQuery(OmtsDbContext db) : IPurchaseRequestFulfilment
{
    public async Task<IReadOnlySet<Guid>> FulfilledAmongAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken)
    {
        var ids = requestIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var fulfilled = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.PurchaseRequestId != null && ids.Contains(i.PurchaseRequestId.Value) && i.TakesRequestInFull && i.StatusId == StatusIds.SalesInvoice.Completed)
            .Select(i => i.PurchaseRequestId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        return fulfilled.ToHashSet();
    }
}

/// <summary>
/// The four sums of BR-DEBT-02 per profile: invoices not cancelled at their latest total, and payments not cancelled (Awaiting confirmation included) by kind. Walk-in invoices and payments
/// without a customer have no profile and count nowhere. Sales returns come with CLAIM-03 (0 until then).
/// </summary>
internal sealed class CustomerDebtQuery(OmtsDbContext db) : ICustomerDebtQuery
{
    public async Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> FiguresAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        var invoices = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.CustomerId != null && ids.Contains(i.CustomerId.Value) && i.StatusId != StatusIds.SalesInvoice.Cancelled)
            .GroupBy(i => i.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(i => i.Total) })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Total, cancellationToken);
        var payments = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.CustomerId != null && ids.Contains(p.CustomerId.Value) && p.StatusId != StatusIds.CustomerPayment.Cancelled)
            .GroupBy(p => new { CustomerId = p.CustomerId!.Value, p.Kind })
            .Select(g => new { g.Key.CustomerId, g.Key.Kind, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);
        return ids.ToDictionary(id => id, id => new CustomerDebtFigures(
            invoices.GetValueOrDefault(id),
            0m,
            payments.Where(p => p.CustomerId == id && p.Kind == CustomerPaymentKind.Payment).Sum(p => p.Amount),
            payments.Where(p => p.CustomerId == id && p.Kind == CustomerPaymentKind.PaidBack).Sum(p => p.Amount)));
    }
}
