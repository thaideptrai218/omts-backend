using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>A sales invoice of a customer profile, cancelled ones included (the statement lists them; the debt and the allocation skip them).</summary>
/// <param name="Total">The total at its latest values (BR-SALE-10).</param>
/// <param name="CancelledAt">When SALE-08 cancelled it, shown in the statement (BR-DEBT-04).</param>
/// <param name="CancelReason">Why it was cancelled.</param>
public sealed record CustomerLedgerInvoice(
    Guid CustomerId, Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, decimal Total, bool Cancelled, DateTimeOffset? CancelledAt = null, string? CancelReason = null);

/// <summary>A customer payment or money paid back of a profile, cancelled ones included.</summary>
/// <param name="InvoiceId">The invoice the money came with (cash at the port, VietQR code of the invoice): it covers that invoice first.</param>
/// <param name="InvoiceCode">The code of that invoice, for the statement.</param>
/// <param name="CancelledAt">When it was cancelled (DEBT-02.4), shown in the statement (BR-DEBT-04).</param>
/// <param name="Internals">The staff-only data of the statement of DEBT-03 (BR-DEBT-04); null where it is not read (debt list, allocation).</param>
public sealed record CustomerLedgerPayment(
    Guid CustomerId, Guid Id, string Code, CustomerPaymentKind Kind, DateOnly PaymentDate, decimal Amount, CustomerPaymentMethod Method,
    Guid? InvoiceId, string? InvoiceCode, int StatusId, DateTimeOffset? CancelledAt = null, CustomerPaymentInternals? Internals = null)
{
    public bool Cancelled => StatusId == StatusIds.CustomerPayment.Cancelled;

    public bool AwaitingConfirmation => StatusId == StatusIds.CustomerPayment.AwaitingConfirmation;
}

/// <summary>
/// BR-DEBT-04: the internal data of a payment that the staff statement (DEBT-03) shows and the trader's statement (DEBT-04) leaves out: who recorded and who confirmed it,
/// the Zone Manager who collected the cash, its note and its cancel reason. A null person: the system did it, or nobody yet.
/// </summary>
public sealed record CustomerPaymentInternals(AccountRef? RecordedBy, AccountRef? ConfirmedBy, AccountRef? CollectedBy, string? Note, string? CancelReason);

/// <summary>An invoice not yet fully covered (BR-DEBT-05).</summary>
/// <param name="Covered">What the payments cover of its total.</param>
/// <param name="Unpaid">Total − covered, above 0.</param>
/// <param name="AgeDays">Days since its invoice date.</param>
public sealed record UnpaidInvoice(Guid Id, string Code, DateOnly InvoiceDate, decimal Total, decimal Covered, decimal Unpaid, int AgeDays);

/// <summary>The debt of one profile now and the invoices still unpaid, oldest first.</summary>
/// <param name="Debt">BR-DEBT-02 over the same rows; below 0 it is money owed back.</param>
/// <param name="OldestUnpaidDate">The date the age of the debt counts from; null when the debt is not above 0.</param>
public sealed record CustomerDebtAllocation(decimal Debt, IReadOnlyList<UnpaidInvoice> UnpaidInvoices, DateOnly? OldestUnpaidDate, int? DebtAgeDays)
{
    public static readonly CustomerDebtAllocation None = new(0m, [], null, null);

    /// <summary>What is still unpaid of the invoice: 0 when it is covered, cancelled or of another profile.</summary>
    public decimal UnpaidOf(Guid invoiceId) => UnpaidInvoices.FirstOrDefault(i => i.Id == invoiceId)?.Unpaid ?? 0m;
}

/// <summary>
/// BR-DEBT-05 – which invoices of a customer profile are still unpaid, as a pure function of its rows, so every screen (DEBT-01, DEBT-03, MSG-SALE-15) gives one answer.
/// The debt stays one running total (BR-DEBT-02); this only shows how the money received covers the invoices:
/// (1) each invoice that is not cancelled needs its total at its latest values (a sales return covering it in full comes with CLAIM-03);
/// (2) a payment that came with an invoice covers that invoice first, and what that invoice does not need goes to the pool;
/// (3) every other payment goes to the pool; (4) money paid back is taken from the pool;
/// (5) the pool covers the oldest invoices first (invoice time, then code); a pool below 0 (a payment cancelled after money was paid back) makes the newest covered
/// invoices unpaid again for the difference. Cancelled invoices and payments count nowhere. A merged profile's rows are re-pointed to the kept one (SALE-04.3), so they are
/// allocated together. The unpaid amounts add up to the debt when it is above 0; when it is 0 or below no invoice is unpaid.
/// </summary>
public static class CustomerDebtAllocator
{
    public static CustomerDebtAllocation Allocate(IEnumerable<CustomerLedgerInvoice> invoices, IEnumerable<CustomerLedgerPayment> payments, DateOnly today)
    {
        var open = invoices.Where(i => !i.Cancelled).OrderBy(i => i.InvoicedAt).ThenBy(i => i.Code, StringComparer.Ordinal).ToList();
        var counted = payments.Where(p => !p.Cancelled).ToList();
        var covered = open.ToDictionary(i => i.Id, _ => 0m);
        var needs = open.ToDictionary(i => i.Id, i => i.Total);

        var pool = 0m;
        foreach (var payment in counted)
        {
            if (payment.Kind == CustomerPaymentKind.PaidBack)
            {
                pool -= payment.Amount;
                continue;
            }

            var own = payment.InvoiceId is { } invoiceId && needs.TryGetValue(invoiceId, out var need) ? Math.Min(payment.Amount, need - covered[invoiceId]) : 0m;
            if (own > 0)
            {
                covered[payment.InvoiceId!.Value] += own;
            }

            pool += payment.Amount - own;
        }

        if (pool >= 0)
        {
            foreach (var invoice in open)
            {
                var part = Math.Min(pool, invoice.Total - covered[invoice.Id]);
                covered[invoice.Id] += part;
                pool -= part;
            }
        }
        else
        {
            var shortfall = -pool;
            for (var i = open.Count - 1; i >= 0 && shortfall > 0; i--)
            {
                var part = Math.Min(shortfall, covered[open[i].Id]);
                covered[open[i].Id] -= part;
                shortfall -= part;
            }
        }

        var debt = CustomerDebtMath.Debt(new CustomerDebtFigures(
            open.Sum(i => i.Total),
            0m,
            counted.Where(p => p.Kind == CustomerPaymentKind.Payment).Sum(p => p.Amount),
            counted.Where(p => p.Kind == CustomerPaymentKind.PaidBack).Sum(p => p.Amount)));
        var unpaid = open
            .Where(i => i.Total > covered[i.Id])
            .Select(i => new UnpaidInvoice(i.Id, i.Code, i.InvoiceDate, i.Total, covered[i.Id], i.Total - covered[i.Id], today.DayNumber - i.InvoiceDate.DayNumber))
            .ToList();
        // A debt with no unpaid invoice comes only from money paid back above the payments (no invoice left to explain it): it is as old as that money.
        var oldest = debt <= 0
            ? null
            : unpaid.Count > 0
                ? unpaid[0].InvoiceDate
                : counted.Where(p => p.Kind == CustomerPaymentKind.PaidBack).Select(p => (DateOnly?)p.PaymentDate).Min();
        return new CustomerDebtAllocation(debt, unpaid, oldest, oldest is { } date ? today.DayNumber - date.DayNumber : null);
    }
}
