using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>Kinds of statement entries, in the order they appear on one day. Sales returns come with CLAIM-03.</summary>
public enum CustomerStatementEntryKind
{
    Invoice,
    Return,
    Payment,
    PaidBack,
}

/// <param name="Amount">The amount as recorded (invoice total at its latest values, payment amount), above 0.</param>
/// <param name="DebtChange">What it changes in the debt: above 0 raises it, below 0 lowers it, 0 when cancelled.</param>
/// <param name="InvoiceCode">The invoice a payment came with.</param>
/// <param name="Method">Payments and money paid back only.</param>
/// <param name="CancelledAt">When a cancelled invoice or payment was cancelled (BR-DEBT-04).</param>
/// <param name="RecordedBy">Payments and money paid back only, staff statement only (BR-DEBT-04): who recorded it; null when the system did.</param>
/// <param name="ConfirmedBy">Who confirmed it; null while Awaiting confirmation.</param>
/// <param name="CollectedBy">The Zone Manager who collected the cash at the port.</param>
/// <param name="Note">The note of the payment.</param>
/// <param name="CancelReason">The reason of its cancellation (DEBT-02.4, SALE-08).</param>
/// <param name="Corrections">An invoice only: its corrections and price cuts, also those made after the period (BR-DEBT-04).</param>
public sealed record CustomerStatementEntry(
    DateOnly Date, CustomerStatementEntryKind Kind, Guid Id, string Code, decimal Amount, decimal DebtChange, string? InvoiceCode,
    CustomerPaymentMethod? Method, bool AwaitingConfirmation, bool Cancelled, decimal DebtAfter, DateTimeOffset? CancelledAt = null,
    AccountRef? RecordedBy = null, AccountRef? ConfirmedBy = null, AccountRef? CollectedBy = null, string? Note = null, string? CancelReason = null,
    IReadOnlyList<SalesInvoiceCorrectionView>? Corrections = null);

/// <summary>The debt at the start of a period, its entries in date order and the debt at its end (BR-DEBT-04).</summary>
public sealed record CustomerStatementLines(decimal OpeningDebt, IReadOnlyList<CustomerStatementEntry> Entries, decimal ClosingDebt);

/// <summary>
/// BR-DEBT-04 as a pure function: the debt at the start of the period counts the entries dated before it; each entry of the period is followed by the debt after it.
/// A cancelled invoice or payment stays on its own date, marked Cancelled, and counts 0, so a past period can change after a later cancellation. Same day: invoices,
/// then returns, payments and money paid back, each by code.
/// </summary>
public static class CustomerStatementLedger
{
    public static CustomerStatementLines Build(
        IEnumerable<CustomerLedgerInvoice> invoices, IEnumerable<CustomerLedgerPayment> payments, DateOnly from, DateOnly to,
        IReadOnlyDictionary<Guid, IReadOnlyList<SalesInvoiceCorrectionView>>? corrections = null)
    {
        var all = invoices
            .Select(i => new CustomerStatementEntry(
                i.InvoiceDate, CustomerStatementEntryKind.Invoice, i.Id, i.Code, i.Total, i.Cancelled ? 0m : i.Total, null, null, false, i.Cancelled, 0m,
                i.CancelledAt, CancelReason: i.CancelReason, Corrections: corrections?.GetValueOrDefault(i.Id)))
            .Concat(payments.Select(p => new CustomerStatementEntry(
                p.PaymentDate,
                p.Kind == CustomerPaymentKind.Payment ? CustomerStatementEntryKind.Payment : CustomerStatementEntryKind.PaidBack,
                p.Id, p.Code, p.Amount, p.Cancelled ? 0m : p.Kind == CustomerPaymentKind.Payment ? -p.Amount : p.Amount,
                p.InvoiceCode, p.Method, p.AwaitingConfirmation, p.Cancelled, 0m, p.CancelledAt,
                p.Internals?.RecordedBy, p.Internals?.ConfirmedBy, p.Internals?.CollectedBy, p.Internals?.Note, p.Internals?.CancelReason)))
            .ToList();

        var opening = all.Where(e => e.Date < from).Sum(e => e.DebtChange);
        var debt = opening;
        var entries = new List<CustomerStatementEntry>();
        foreach (var entry in all.Where(e => e.Date >= from && e.Date <= to).OrderBy(e => e.Date).ThenBy(e => e.Kind).ThenBy(e => e.Code, StringComparer.Ordinal))
        {
            debt += entry.DebtChange;
            entries.Add(entry with { DebtAfter = debt });
        }

        return new CustomerStatementLines(opening, entries, debt);
    }
}
