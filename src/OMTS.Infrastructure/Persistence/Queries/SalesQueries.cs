using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Validation;
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
        var figures = await SumsAsync(ids, cancellationToken);
        return ids.ToDictionary(id => id, id => figures.GetValueOrDefault(id, CustomerDebtFigures.None));
    }

    public Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> AllFiguresAsync(CancellationToken cancellationToken) => SumsAsync(null, cancellationToken);

    public async Task<IReadOnlyList<DebtCustomerRow>> CustomersAsync(IReadOnlyCollection<Guid> customerIds, string? search, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        var query = db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            // As SALE-04 step 2: a number typed as +84…, with spaces or dots, is also looked up as stored.
            var phone = PhoneNumbers.Normalize(text) ?? text;
            query = query.Where(c => c.Name.Contains(text) || (c.PhoneNumber != null && (c.PhoneNumber.Contains(text) || c.PhoneNumber.Contains(phone))));
        }

        return await query.Select(c => new DebtCustomerRow(c.Id, c.Name, c.PhoneNumber)).ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<CustomerLedgerInvoice> Invoices, IReadOnlyList<CustomerLedgerPayment> Payments)> LedgerAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return ([], []);
        }

        var invoices = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.CustomerId != null && ids.Contains(i.CustomerId.Value))
            .Select(i => new CustomerLedgerInvoice(i.CustomerId!.Value, i.Id, i.Code, i.InvoicedAt, i.InvoiceDate, i.Total, i.StatusId == StatusIds.SalesInvoice.Cancelled))
            .ToListAsync(cancellationToken);
        var payments = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.CustomerId != null && ids.Contains(p.CustomerId.Value))
            .Select(p => new CustomerLedgerPayment(
                p.CustomerId!.Value, p.Id, p.Code, p.Kind, p.PaymentDate, p.Amount, p.Method, p.InvoiceId, p.Invoice == null ? null : p.Invoice.Code, p.StatusId, p.CancelledAt))
            .ToListAsync(cancellationToken);
        return (invoices, payments);
    }

    public Task<int> AwaitingConfirmationCountAsync(CancellationToken cancellationToken) =>
        db.CustomerPayments.AsNoTracking().CountAsync(p => p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation, cancellationToken);

    /// <summary>One grouped sum of invoices and one of payments, for the profiles asked for or for every profile; a profile with no row has no entry.</summary>
    private async Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> SumsAsync(List<Guid>? ids, CancellationToken cancellationToken)
    {
        var invoiceRows = db.SalesInvoices.AsNoTracking().Where(i => i.CustomerId != null && i.StatusId != StatusIds.SalesInvoice.Cancelled);
        var paymentRows = db.CustomerPayments.AsNoTracking().Where(p => p.CustomerId != null && p.StatusId != StatusIds.CustomerPayment.Cancelled);
        if (ids is not null)
        {
            invoiceRows = invoiceRows.Where(i => ids.Contains(i.CustomerId!.Value));
            paymentRows = paymentRows.Where(p => ids.Contains(p.CustomerId!.Value));
        }

        var invoices = await invoiceRows
            .GroupBy(i => i.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(i => i.Total) })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Total, cancellationToken);
        var payments = await paymentRows
            .GroupBy(p => new { CustomerId = p.CustomerId!.Value, p.Kind })
            .Select(g => new { g.Key.CustomerId, g.Key.Kind, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);
        var paid = payments.Where(p => p.Kind == CustomerPaymentKind.Payment).ToDictionary(p => p.CustomerId, p => p.Amount);
        var paidBack = payments.Where(p => p.Kind == CustomerPaymentKind.PaidBack).ToDictionary(p => p.CustomerId, p => p.Amount);
        return invoices.Keys.Union(paid.Keys).Union(paidBack.Keys).ToDictionary(id => id, id => new CustomerDebtFigures(
            invoices.GetValueOrDefault(id), 0m, paid.GetValueOrDefault(id), paidBack.GetValueOrDefault(id)));
    }
}
