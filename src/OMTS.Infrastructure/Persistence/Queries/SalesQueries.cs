using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// What HARVEST reads of the sales (BR-HARVEST-07, BR-HARVEST-05): the kg on lines of invoices that are not cancelled, at their latest kg, and the returned kg of the sales returns that stand,
/// split into back for sale and discarded (AC-09: 600 invoiced - 300 returned = 300 sold). A lot on any invoice, a cancelled one included (it keeps its lines), or on a sales return can no longer be removed.
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
        var returned = ids.Count == 0
            ? []
            : await db.SalesReturns.AsNoTracking().Where(SalesReturn.Standing).SelectMany(r => r.Lines)
                .Where(l => ids.Contains(l.HarvestLotId))
                .GroupBy(l => l.HarvestLotId)
                .Select(g => new { LotId = g.Key, Returned = g.Sum(l => l.ReturnedKg), Discarded = g.Sum(l => l.DiscardedKg) })
                .ToDictionaryAsync(x => x.LotId, cancellationToken);
        return ids.ToDictionary(id => id, id =>
        {
            var back = returned.GetValueOrDefault(id);
            return new LotSalesFigures(invoiced.GetValueOrDefault(id), back?.Returned ?? 0m, (back?.Returned ?? 0m) - (back?.Discarded ?? 0m), back?.Discarded ?? 0m);
        });
    }

    public async Task<bool> IsOnAnyInvoiceAsync(Guid lotId, CancellationToken cancellationToken) =>
        await db.SalesInvoiceLines.AsNoTracking().AnyAsync(l => l.HarvestLotId == lotId, cancellationToken)
            || await db.SalesReturnLines.AsNoTracking().AnyAsync(l => l.HarvestLotId == lotId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> LotsOnAnyInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        var returned = ids.Count == 0
            ? []
            : await db.SalesReturnLines.AsNoTracking().Where(l => ids.Contains(l.HarvestLotId)).Select(l => l.HarvestLotId).Distinct().ToListAsync(cancellationToken);
        return (await LotsOnAsync(db.SalesInvoiceLines.AsNoTracking(), lotIds, cancellationToken)).Union(returned).ToHashSet();
    }

    public Task<IReadOnlySet<Guid>> LotsOnCurrentInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        LotsOnAsync(db.SalesInvoiceLines.AsNoTracking().Where(l => l.Invoice.StatusId != StatusIds.SalesInvoice.Cancelled), lotIds, cancellationToken);

    private static async Task<IReadOnlySet<Guid>> LotsOnAsync(IQueryable<SalesInvoiceLine> lines, IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        return ids.Count == 0
            ? new HashSet<Guid>()
            : (await lines.Where(l => ids.Contains(l.HarvestLotId)).Select(l => l.HarvestLotId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
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

/// <summary>"Has a sales return" (BR-CLAIM-01, BR-CLAIM-02) by the same rule as <see cref="SalesInvoiceAllowedActions.HasSalesReturn"/>: <see cref="SalesReturn.Standing"/>.</summary>
internal sealed class InvoiceSalesReturnCheck(OmtsDbContext db) : IInvoiceSalesReturnCheck
{
    public Task<bool> HasSalesReturnAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.SalesReturns.AsNoTracking().Where(r => r.InvoiceId == invoiceId).AnyAsync(SalesReturn.Standing, cancellationToken);
}

/// <summary>
/// The sums of BR-DEBT-02 per profile: invoices not cancelled at their latest total, payments not cancelled (Awaiting confirmation included, Pending transfer left out) by kind with their discounts,
/// and the debt adjustments not cancelled. Walk-in invoices and payments without a customer have no profile and count nowhere. The sales returns that stand are taken off at their value.
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

        return await query.Select(c => new DebtCustomerRow(c.Id, c.Name, c.PhoneNumber, c.Code)).ToListAsync(cancellationToken);
    }

    public async Task<CustomerLedgerRows> LedgerAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new CustomerLedgerRows([], [], []);
        }

        var invoices = await InvoicesOf(ids, cancellationToken);
        var payments = await PaymentsOf(ids)
            .Select(p => new CustomerLedgerPayment(
                p.CustomerId!.Value, p.Id, p.Code, p.Kind, p.PaymentDate, p.Amount, p.Method, p.InvoiceId, p.Invoice == null ? null : p.Invoice.Code, p.StatusId, p.CancelledAt, null,
                p.PaidAt, p.DiscountAmount, null))
            .ToListAsync(cancellationToken);
        return new CustomerLedgerRows(invoices, await WithAllocationsAsync(payments, ids, cancellationToken), await AdjustmentsOf(ids, false, cancellationToken));
    }

    public async Task<CustomerLedgerRows> StatementLedgerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        List<Guid> ids = [customerId];
        var invoices = await InvoicesOf(ids, cancellationToken);
        // The display names are scalar subqueries of the same command (no query per payment, no extra command).
        var rows = await PaymentsOf(ids)
            .Select(p => new
            {
                Ledger = new CustomerLedgerPayment(
                    p.CustomerId!.Value, p.Id, p.Code, p.Kind, p.PaymentDate, p.Amount, p.Method, p.InvoiceId, p.Invoice == null ? null : p.Invoice.Code, p.StatusId, p.CancelledAt, null,
                    p.PaidAt, p.DiscountAmount, null),
                p.Note,
                p.CancelReason,
                p.RecordedByAccountId,
                RecordedByName = db.Accounts.Where(a => a.Id == p.RecordedByAccountId).Select(a => a.DisplayName).FirstOrDefault(),
                p.ConfirmedByAccountId,
                ConfirmedByName = db.Accounts.Where(a => a.Id == p.ConfirmedByAccountId).Select(a => a.DisplayName).FirstOrDefault(),
                p.CollectedByAccountId,
                CollectedByName = db.Accounts.Where(a => a.Id == p.CollectedByAccountId).Select(a => a.DisplayName).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        static AccountRef? Ref(Guid? id, string? name) => id is { } value ? new AccountRef(value, name ?? string.Empty) : null;
        var payments = rows
            .Select(r => r.Ledger with
            {
                Internals = new CustomerPaymentInternals(
                    Ref(r.RecordedByAccountId, r.RecordedByName), Ref(r.ConfirmedByAccountId, r.ConfirmedByName), Ref(r.CollectedByAccountId, r.CollectedByName),
                    r.Note, r.CancelReason),
            })
            .ToList();
        return new CustomerLedgerRows(invoices, await WithAllocationsAsync(payments, ids, cancellationToken), await AdjustmentsOf(ids, true, cancellationToken));
    }

    public async Task<IReadOnlyList<CustomerLedgerReturn>> ReturnsOfAsync(Guid customerId, CancellationToken cancellationToken) =>
        await db.SalesReturns.AsNoTracking()
            .Where(r => r.Invoice.CustomerId == customerId)
            .Select(r => new CustomerLedgerReturn(
                customerId, r.Id, r.Code, r.InvoiceId, r.Invoice.Code, r.ReturnDate, r.Value, r.StatusId == StatusIds.SalesReturn.Cancelled, r.CancelledAt, r.CancelReason))
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<CustomerLedgerInvoice>> InvoicesOf(List<Guid> ids, CancellationToken cancellationToken)
    {
        var invoices = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.CustomerId != null && ids.Contains(i.CustomerId.Value))
            .Select(i => new CustomerLedgerInvoice(i.CustomerId!.Value, i.Id, i.Code, i.InvoicedAt, i.InvoiceDate, i.Total, i.StatusId == StatusIds.SalesInvoice.Cancelled, null, i.CancelledAt, i.CancelReason,
                i.Returns.Where(r => r.StatusId == StatusIds.SalesReturn.Recorded).Sum(r => r.Value)))
            .ToListAsync(cancellationToken);
        var cuts = await db.SalesInvoicePriceCuts.AsNoTracking()
            .Where(c => c.Invoice.CustomerId != null && ids.Contains(c.Invoice.CustomerId.Value))
            .Select(c => new { c.InvoiceId, Cut = new CustomerLedgerPriceCut(c.Id, c.CutDate, c.Amount, c.Reason) })
            .ToListAsync(cancellationToken);
        if (cuts.Count == 0)
        {
            return invoices;
        }

        var cutsOf = cuts.ToLookup(c => c.InvoiceId, c => c.Cut);
        return invoices.Select(i => cutsOf.Contains(i.Id) ? i with { PriceCuts = cutsOf[i.Id].OrderBy(c => c.CutDate).ToList() } : i).ToList();
    }

    private async Task<IReadOnlyList<CustomerLedgerPayment>> WithAllocationsAsync(List<CustomerLedgerPayment> payments, List<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (payments.Count == 0)
        {
            return payments;
        }

        var parts = await db.CustomerPaymentAllocations.AsNoTracking()
            .Where(a => a.Payment.CustomerId != null && customerIds.Contains(a.Payment.CustomerId.Value))
            .Select(a => new { a.PaymentId, Part = new CustomerLedgerAllocation(a.InvoiceId, a.Amount) })
            .ToListAsync(cancellationToken);
        if (parts.Count == 0)
        {
            return payments;
        }

        var partsOf = parts.ToLookup(a => a.PaymentId, a => a.Part);
        return payments.Select(p => partsOf.Contains(p.Id) ? p with { Allocations = partsOf[p.Id].ToList() } : p).ToList();
    }

    /// <param name="named">True for the staff statement, which shows who recorded each one.</param>
    private async Task<IReadOnlyList<CustomerLedgerAdjustment>> AdjustmentsOf(List<Guid> ids, bool withNames, CancellationToken cancellationToken)
    {
        var rows = await db.CustomerDebtAdjustments.AsNoTracking()
            .Where(a => ids.Contains(a.CustomerId))
            .Select(a => new
            {
                a.CustomerId, a.Id, a.Code, a.AdjustmentDate, a.OccurredAt, a.Amount, a.Reason, a.CancelledAt, a.RecordedByAccountId,
                RecordedByName = db.Accounts.Where(x => x.Id == a.RecordedByAccountId).Select(x => x.DisplayName).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return rows.Select(a => new CustomerLedgerAdjustment(
            a.CustomerId, a.Id, a.Code, a.AdjustmentDate, a.OccurredAt, a.Amount, a.Reason, a.CancelledAt != null, a.CancelledAt,
            withNames ? new AccountRef(a.RecordedByAccountId, a.RecordedByName ?? string.Empty) : null)).ToList();
    }

    private IQueryable<CustomerPayment> PaymentsOf(List<Guid> ids) =>
        db.CustomerPayments.AsNoTracking().Where(p => p.CustomerId != null && ids.Contains(p.CustomerId.Value));

    public Task<int> AwaitingConfirmationCountAsync(CancellationToken cancellationToken) =>
        db.CustomerPayments.AsNoTracking().CountAsync(p => p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation, cancellationToken);

    /// <summary>One grouped sum of invoices and one of payments, for the profiles asked for or for every profile; a profile with no row has no entry.</summary>
    private async Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> SumsAsync(List<Guid>? ids, CancellationToken cancellationToken)
    {
        var invoiceRows = db.SalesInvoices.AsNoTracking().Where(i => i.CustomerId != null && i.StatusId != StatusIds.SalesInvoice.Cancelled);
        // A payment waiting for its transfer counts nowhere.
        var paymentRows = db.CustomerPayments.AsNoTracking()
            .Where(p => p.CustomerId != null && p.StatusId != StatusIds.CustomerPayment.Cancelled && p.StatusId != StatusIds.CustomerPayment.PendingTransfer);
        var adjustmentRows = db.CustomerDebtAdjustments.AsNoTracking().Where(a => a.CancelledAt == null);
        var returnRows = db.SalesReturns.AsNoTracking().Where(SalesReturn.Standing).Where(r => r.Invoice.CustomerId != null);
        if (ids is not null)
        {
            invoiceRows = invoiceRows.Where(i => ids.Contains(i.CustomerId!.Value));
            paymentRows = paymentRows.Where(p => ids.Contains(p.CustomerId!.Value));
            adjustmentRows = adjustmentRows.Where(a => ids.Contains(a.CustomerId));
            returnRows = returnRows.Where(r => ids.Contains(r.Invoice.CustomerId!.Value));
        }

        var invoices = await invoiceRows
            .GroupBy(i => i.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(i => i.Total) })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Total, cancellationToken);
        var returns = await returnRows
            .GroupBy(r => r.Invoice.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Value = g.Sum(r => r.Value) })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Value, cancellationToken);
        var payments = await paymentRows
            .GroupBy(p => new { CustomerId = p.CustomerId!.Value, p.Kind })
            .Select(g => new { g.Key.CustomerId, g.Key.Kind, Amount = g.Sum(p => p.Amount), Discount = g.Sum(p => p.DiscountAmount) })
            .ToListAsync(cancellationToken);
        var adjusted = await adjustmentRows
            .GroupBy(a => a.CustomerId)
            .Select(g => new { CustomerId = g.Key, Amount = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Amount, cancellationToken);
        var paid = payments.Where(p => p.Kind == CustomerPaymentKind.Payment).ToDictionary(p => p.CustomerId, p => p.Amount);
        var discounts = payments.Where(p => p.Kind == CustomerPaymentKind.Payment).ToDictionary(p => p.CustomerId, p => p.Discount);
        var paidBack = payments.Where(p => p.Kind == CustomerPaymentKind.PaidBack).ToDictionary(p => p.CustomerId, p => p.Amount);
        return invoices.Keys.Union(paid.Keys).Union(paidBack.Keys).Union(adjusted.Keys).ToDictionary(id => id, id => new CustomerDebtFigures(
            invoices.GetValueOrDefault(id), returns.GetValueOrDefault(id), paid.GetValueOrDefault(id), paidBack.GetValueOrDefault(id), discounts.GetValueOrDefault(id), adjusted.GetValueOrDefault(id)));
    }
}
