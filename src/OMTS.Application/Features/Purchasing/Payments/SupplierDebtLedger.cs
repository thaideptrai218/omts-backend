using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>Kinds of statement entries, in the order they appear on one day.</summary>
public enum SupplierStatementEntryKind
{
    Receipt,
    Return,
    Payment,
    PaidBack,
}

/// <param name="Amount">Signed: above zero raises the debt, below zero lowers it.</param>
/// <param name="ReceiptCode">The receipt of a return.</param>
public sealed record SupplierStatementEntry(
    DateOnly Date, SupplierStatementEntryKind Kind, Guid Id, string Code, string? ReceiptCode,
    PaymentMethod? Method, string? BankReference, string? Note, decimal Amount, decimal DebtAfter);

/// <param name="Covered">What the payments cover of this receipt, oldest receipts first.</param>
/// <param name="Remaining">Total − returned − covered, above zero.</param>
public sealed record UnpaidReceipt(Guid Id, string Code, DateOnly ReceiptDate, decimal Total, decimal Returned, decimal Covered, decimal Remaining, int AgeDays);

/// <summary>
/// The supplier debt rules of BR-PURCHASE-04, as pure functions: a running total with no link between payments and receipts.
/// To show what is still unpaid, a return lowers its own receipt and the money the supplier holds covers the oldest receipts
/// first, also receipts dated after the payment (an advance is used up by the next receipts, BR-PURCHASE-08).
/// </summary>
public static class SupplierDebtLedger
{
    /// <summary>The Completed receipts not yet fully covered, oldest first.</summary>
    /// <param name="credit">What the supplier holds (<see cref="SupplierDebtSums.Credit"/>); nothing is covered when it is zero or below.</param>
    public static IReadOnlyList<UnpaidReceipt> UnpaidReceipts(IEnumerable<DebtReceiptRow> receipts, decimal credit, DateOnly today)
    {
        var rows = receipts.OrderBy(r => r.ReceiptDate).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
        // Taken as credit before the walk, so it also covers receipts older than the return, and what stays unpaid adds up to the debt.
        credit += ReturnExcess(rows);
        var unpaid = new List<UnpaidReceipt>();
        foreach (var receipt in rows)
        {
            var net = receipt.Total - receipt.Returned;
            if (net <= 0)
            {
                continue;
            }

            var covered = Math.Min(net, Math.Max(credit, 0));
            credit -= covered;
            if (net > covered)
            {
                unpaid.Add(new UnpaidReceipt(
                    receipt.Id, receipt.Code, receipt.ReceiptDate, receipt.Total, receipt.Returned, covered, net - covered,
                    today.DayNumber - receipt.ReceiptDate.DayNumber));
            }
        }

        return unpaid;
    }

    /// <summary>
    /// What the returns of the receipts are worth above those receipts. It lowers the debt like a payment, because a return
    /// keeps its amount (PM 03/11): a later lower unit price leaves it above the receipt, and so can parts of a line rounded one by one.
    /// </summary>
    public static decimal ReturnExcess(IEnumerable<DebtReceiptRow> receipts) => receipts.Sum(r => Math.Max(0, r.Returned - r.Total));

    /// <summary>
    /// The date a debt with no unpaid receipt comes from: the supplier paid back more than it was paid, and the debt is that
    /// excess. What it holds (payments and <see cref="ReturnExcess"/>) covers the oldest amounts paid back first; null when all
    /// of them are covered (gap OQ-6).
    /// </summary>
    public static DateOnly? OldestUncoveredMoneyBack(IEnumerable<DebtMoneyBackRow> moneyBack, decimal covering)
    {
        foreach (var row in moneyBack.OrderBy(m => m.Date))
        {
            if (row.Amount > covering)
            {
                return row.Date;
            }

            covering -= row.Amount;
        }

        return null;
    }

    /// <summary>PURCHASE-04 step 3: the entries of a period in date order, each with the debt after it.</summary>
    public static IReadOnlyList<SupplierStatementEntry> Statement(
        decimal openingDebt, IEnumerable<DebtReceiptRow> receipts, IEnumerable<StatementReturnRow> returns, IEnumerable<StatementPaymentRow> payments)
    {
        var rows = receipts
            .Select(r => Entry(r.ReceiptDate, SupplierStatementEntryKind.Receipt, r.Id, r.Code, r.Total, null))
            .Concat(returns.Select(r => Entry(r.ReturnDate, SupplierStatementEntryKind.Return, r.Id, r.Code, -r.Value, r.ReceiptCode)))
            .Concat(payments.Select(p => new SupplierStatementEntry(
                p.PaymentDate,
                p.Kind == SupplierPaymentKind.Payment ? SupplierStatementEntryKind.Payment : SupplierStatementEntryKind.PaidBack,
                p.Id, p.Code, null, p.Method, p.BankReference, p.Note,
                p.Kind == SupplierPaymentKind.Payment ? -p.Amount : p.Amount, 0)))
            .OrderBy(e => e.Date).ThenBy(e => e.Kind).ThenBy(e => e.Code, StringComparer.Ordinal);

        var debt = openingDebt;
        var entries = new List<SupplierStatementEntry>();
        foreach (var row in rows)
        {
            debt += row.Amount;
            entries.Add(row with { DebtAfter = debt });
        }

        return entries;
    }

    private static SupplierStatementEntry Entry(DateOnly date, SupplierStatementEntryKind kind, Guid id, string code, decimal amount, string? receiptCode) =>
        new(date, kind, id, code, receiptCode, null, null, null, amount, 0);
}
