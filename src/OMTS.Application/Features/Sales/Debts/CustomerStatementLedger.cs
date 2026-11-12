using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>Kinds of statement entries, in the order they appear on one day. Sales returns come with CLAIM-03.</summary>
public enum CustomerStatementEntryKind
{
    Invoice,
    PriceCut,
    Return,
    Payment,
    Discount,
    PaidBack,
    Adjustment,
}

/// <param name="Amount">The amount as recorded (invoice total before its price cuts, payment amount, discount, adjustment without its sign), above 0.</param>
/// <param name="DebtChange">What it changes in the debt: above 0 raises it, below 0 lowers it, 0 when cancelled or while a payment waits for its transfer.</param>
/// <param name="InvoiceCode">The invoice a payment came with, or the invoice of a price cut.</param>
/// <param name="Method">Payments and money paid back only.</param>
/// <param name="CancelledAt">When a cancelled invoice or payment was cancelled (BR-DEBT-04).</param>
/// <param name="RecordedBy">Payments, money paid back and adjustments, staff statement only (BR-DEBT-04): who recorded it; null when the system did.</param>
/// <param name="ConfirmedBy">Who confirmed it; null while Awaiting confirmation.</param>
/// <param name="CollectedBy">The person who collected the money ("Người thu"), or the Zone Manager who collected the cash at the port.</param>
/// <param name="Note">The note of the payment, or the reason of an adjustment or price cut.</param>
/// <param name="CancelReason">The reason of its cancellation (DEBT-02.4, SALE-08).</param>
/// <param name="At">When it happened: the invoice time, the time the money moved, the time of the adjustment; null for a price cut (a date only).</param>
/// <param name="Pending">A payment waiting for its bank transfer: it changes nothing yet.</param>
/// <param name="Corrections">An invoice only: its corrections, also those made after the period (BR-DEBT-04).</param>
public sealed record CustomerStatementEntry(
    DateOnly Date, CustomerStatementEntryKind Kind, Guid Id, string Code, decimal Amount, decimal DebtChange, string? InvoiceCode,
    CustomerPaymentMethod? Method, bool AwaitingConfirmation, bool Cancelled, decimal DebtAfter, DateTimeOffset? CancelledAt = null,
    AccountRef? RecordedBy = null, AccountRef? ConfirmedBy = null, AccountRef? CollectedBy = null, string? Note = null, string? CancelReason = null,
    DateTimeOffset? At = null, bool Pending = false, IReadOnlyList<SalesInvoiceCorrectionView>? Corrections = null);

/// <summary>The debt at the start of a period, its entries in date order and the debt at its end (BR-DEBT-04).</summary>
public sealed record CustomerStatementLines(decimal OpeningDebt, IReadOnlyList<CustomerStatementEntry> Entries, decimal ClosingDebt);

/// <summary>
/// BR-DEBT-04 as a pure function: the debt at the start of the period counts the entries dated before it; each entry of the period is followed by the debt after it.
/// A cancelled invoice, payment or adjustment stays on its own date, marked Cancelled, and counts 0, so a past period can change after a later cancellation; so does a
/// payment that waits for its transfer, until the transfer arrives. A payment with a discount gives two entries (the money and the discount), an invoice with price cuts
/// one entry for what it was worth and one per cut. Same day: invoices, price cuts, returns, payments, discounts, money paid back and adjustments, each by time, then code.
/// </summary>
public static class CustomerStatementLedger
{
    public static CustomerStatementLines Build(
        IEnumerable<CustomerLedgerInvoice> invoices, IEnumerable<CustomerLedgerPayment> payments, DateOnly from, DateOnly to,
        IEnumerable<CustomerLedgerAdjustment>? adjustments = null, IReadOnlyDictionary<Guid, IReadOnlyList<SalesInvoiceCorrectionView>>? corrections = null)
    {
        var all = new List<CustomerStatementEntry>();
        foreach (var i in invoices)
        {
            all.Add(new CustomerStatementEntry(
                i.InvoiceDate, CustomerStatementEntryKind.Invoice, i.Id, i.Code, i.OriginalTotal, i.Cancelled ? 0m : i.OriginalTotal, null, null, false, i.Cancelled, 0m,
                i.CancelledAt, CancelReason: i.CancelReason, At: i.InvoicedAt, Corrections: corrections?.GetValueOrDefault(i.Id)));
            foreach (var cut in i.PriceCuts ?? [])
            {
                all.Add(new CustomerStatementEntry(
                    cut.CutDate, CustomerStatementEntryKind.PriceCut, cut.Id, i.Code, cut.Amount, i.Cancelled ? 0m : -cut.Amount, i.Code, null, false, i.Cancelled, 0m, Note: cut.Reason));
            }
        }

        foreach (var p in payments)
        {
            var paidBack = p.Kind == CustomerPaymentKind.PaidBack;
            var kind = paidBack ? CustomerStatementEntryKind.PaidBack : CustomerStatementEntryKind.Payment;
            if (p.Amount > 0)
            {
                all.Add(new CustomerStatementEntry(
                    p.PaymentDate, kind, p.Id, p.Code, p.Amount, !p.Counts ? 0m : paidBack ? p.Amount : -p.Amount,
                    p.InvoiceCode, p.Method, p.AwaitingConfirmation, p.Cancelled, 0m, p.CancelledAt,
                    p.Internals?.RecordedBy, p.Internals?.ConfirmedBy, p.Internals?.CollectedBy, p.Internals?.Note, p.Internals?.CancelReason, p.PaidAt, p.Pending));
            }

            if (p.DiscountAmount > 0)
            {
                all.Add(new CustomerStatementEntry(
                    p.PaymentDate, CustomerStatementEntryKind.Discount, p.Id, p.Code, p.DiscountAmount, !p.Counts ? 0m : -p.DiscountAmount,
                    p.InvoiceCode, p.Method, p.AwaitingConfirmation, p.Cancelled, 0m, p.CancelledAt,
                    p.Internals?.RecordedBy, p.Internals?.ConfirmedBy, p.Internals?.CollectedBy, p.Internals?.Note, p.Internals?.CancelReason, p.PaidAt, p.Pending));
            }
        }

        foreach (var a in adjustments ?? [])
        {
            all.Add(new CustomerStatementEntry(
                a.AdjustmentDate, CustomerStatementEntryKind.Adjustment, a.Id, a.Code, Math.Abs(a.Amount), a.Cancelled ? 0m : a.Amount, null, null, false, a.Cancelled, 0m,
                a.CancelledAt, a.RecordedBy, Note: a.Reason, At: a.OccurredAt));
        }

        var opening = all.Where(e => e.Date < from).Sum(e => e.DebtChange);
        var debt = opening;
        var entries = new List<CustomerStatementEntry>();
        foreach (var entry in all.Where(e => e.Date >= from && e.Date <= to)
            .OrderBy(e => e.Date).ThenBy(e => e.Kind).ThenBy(e => e.At ?? DateTimeOffset.MinValue).ThenBy(e => e.Code, StringComparer.Ordinal))
        {
            debt += entry.DebtChange;
            entries.Add(entry with { DebtAfter = debt });
        }

        return new CustomerStatementLines(opening, entries, debt);
    }
}
