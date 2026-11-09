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
/// <param name="ReceiptCode">The receipt of a return; null on a return made without a receipt.</param>
public sealed record SupplierStatementEntry(
    DateOnly Date, SupplierStatementEntryKind Kind, Guid Id, string Code, string? ReceiptCode,
    PaymentMethod? Method, string? BankReference, string? Note, decimal Amount, decimal DebtAfter);

/// <param name="Covered">What the payments cover of this receipt: the parts set against it first, then the rest oldest receipts first.</param>
/// <param name="Remaining">Total − discount − returned − covered, above zero.</param>
/// <param name="ReceivedAt">When the goods were received, at UTC+7.</param>
public sealed record UnpaidReceipt(
    Guid Id, string Code, DateOnly ReceiptDate, decimal Total, decimal Returned, decimal Covered, decimal Remaining, int AgeDays, decimal Discount = 0,
    DateTimeOffset ReceivedAt = default);

/// <summary>The part of a payment read as covering one receipt.</summary>
public sealed record PaymentAllocation(Guid ReceiptId, Guid PaymentId, decimal Amount);

/// <summary>
/// The supplier debt rules of BR-PURCHASE-04, as pure functions: the debt is a running total, which the parts of payments set
/// against receipts never change. To show what is still unpaid, a return lowers its own receipt, a return without a receipt counts
/// as credit, the part of a payment set against a receipt covers that receipt first, and the rest of what the supplier holds covers
/// the oldest receipts first, also receipts dated after the payment (an advance is used up by the next receipts, BR-PURCHASE-08).
/// A part set against a receipt since cancelled, or above what the receipt now adds to the debt, is read as an advance.
/// </summary>
public static class SupplierDebtLedger
{
    /// <summary>The Completed receipts not yet fully covered, oldest first.</summary>
    /// <param name="credit">What the supplier holds (<see cref="SupplierDebtSums.Credit"/>); nothing is covered when it is zero or below.</param>
    /// <param name="allocations">The parts of payments set against receipts; they spend the same credit, so what stays unpaid still adds up to the debt.</param>
    public static IReadOnlyList<UnpaidReceipt> UnpaidReceipts(
        IEnumerable<DebtReceiptRow> receipts, decimal credit, DateOnly today, IEnumerable<DebtAllocationRow>? allocations = null)
    {
        var rows = Oldest(receipts);
        // Taken as credit before the walk, so it also covers receipts older than the return, and what stays unpaid adds up to the debt.
        var available = Math.Max(credit + ReturnExcess(rows), 0);
        var named = Named(rows, allocations);
        var covered = new Dictionary<Guid, decimal>();
        foreach (var receipt in rows)
        {
            var taken = Math.Min(Math.Min(Math.Max(receipt.Net, 0), named.GetValueOrDefault(receipt.Id)), available);
            covered[receipt.Id] = taken;
            available -= taken;
        }

        var unpaid = new List<UnpaidReceipt>();
        foreach (var receipt in rows)
        {
            var net = receipt.Net;
            if (net <= 0)
            {
                continue;
            }

            var total = covered[receipt.Id] + Math.Min(net - covered[receipt.Id], available);
            available -= total - covered[receipt.Id];
            if (net > total)
            {
                unpaid.Add(new UnpaidReceipt(
                    receipt.Id, receipt.Code, receipt.ReceiptDate, receipt.Total, receipt.Returned, total, net - total,
                    today.DayNumber - receipt.ReceiptDate.DayNumber, receipt.Discount, receipt.ReceivedAt));
            }
        }

        return unpaid;
    }

    private static List<DebtReceiptRow> Oldest(IEnumerable<DebtReceiptRow> receipts) =>
        receipts.OrderBy(r => r.ReceiptDate).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();

    /// <summary>Per listed receipt, Σ the parts set against it; a part against a receipt not listed (cancelled since) is left out.</summary>
    private static Dictionary<Guid, decimal> Named(IReadOnlyCollection<DebtReceiptRow> receipts, IEnumerable<DebtAllocationRow>? allocations)
    {
        var ids = receipts.Select(r => r.Id).ToHashSet();
        return (allocations ?? []).Where(a => ids.Contains(a.ReceiptId)).GroupBy(a => a.ReceiptId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
    }

    /// <summary>
    /// What the returns of the receipts are worth above those receipts. It lowers the debt like a payment, because a return
    /// keeps its amount (PM 03/11): a later lower unit price leaves it above the receipt, and so can parts of a line rounded one by one.
    /// </summary>
    public static decimal ReturnExcess(IEnumerable<DebtReceiptRow> receipts) => receipts.Sum(r => Math.Max(0, -r.Net));

    /// <summary>
    /// Which payments cover which receipts, on the same reading as <see cref="UnpaidReceipts"/>: the money paid back comes out of
    /// the newest payments (first of their parts set against no receipt), each part set against a receipt covers that receipt, and
    /// the rest covers the oldest receipts first. Credit that is not money (returns above their receipt or without one) is left out,
    /// so a receipt covered that way shows no payment. One entry per receipt and payment.
    /// </summary>
    public static IReadOnlyList<PaymentAllocation> AllocatePayments(
        IEnumerable<DebtReceiptRow> receipts, IEnumerable<DebtPaymentRow> payments, decimal paidBack, IEnumerable<DebtAllocationRow>? named = null)
    {
        var rows = Oldest(receipts);
        var order = rows.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var ordered = payments.OrderBy(p => p.PaymentDate).ThenBy(p => p.Code, StringComparer.Ordinal).ToList();
        var index = ordered.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var parts = (named ?? []).Where(a => order.ContainsKey(a.ReceiptId) && index.ContainsKey(a.PaymentId))
            .OrderBy(a => order[a.ReceiptId]).ThenBy(a => index[a.PaymentId]).ToList();
        var namedOf = parts.GroupBy(a => a.PaymentId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var pool = ordered.Select(p =>
        {
            var set = Math.Min(namedOf.GetValueOrDefault(p.Id), p.Covers);
            return (p.Id, Free: p.Covers - set, Named: set);
        }).ToArray();
        for (var i = pool.Length - 1; i >= 0 && paidBack > 0; i--)
        {
            var taken = Math.Min(pool[i].Free, paidBack);
            pool[i].Free -= taken;
            paidBack -= taken;
        }

        for (var i = pool.Length - 1; i >= 0 && paidBack > 0; i--)
        {
            var taken = Math.Min(pool[i].Named, paidBack);
            pool[i].Named -= taken;
            paidBack -= taken;
        }

        var due = rows.ToDictionary(r => r.Id, r => Math.Max(r.Net, 0));
        var allocations = new List<PaymentAllocation>();
        foreach (var part in parts)
        {
            var i = index[part.PaymentId];
            var taken = Math.Min(Math.Min(part.Amount, pool[i].Named), due[part.ReceiptId]);
            if (taken > 0)
            {
                allocations.Add(new PaymentAllocation(part.ReceiptId, part.PaymentId, taken));
                pool[i].Named -= taken;
                due[part.ReceiptId] -= taken;
            }
        }

        // What a part could not spend on its receipt is an advance like the rest of the payment.
        var left = pool.Select(p => (p.Id, Left: p.Free + p.Named)).ToArray();
        var next = 0;
        foreach (var receipt in rows)
        {
            var remaining = due[receipt.Id];
            while (remaining > 0 && next < left.Length)
            {
                var taken = Math.Min(remaining, left[next].Left);
                if (taken > 0)
                {
                    allocations.Add(new PaymentAllocation(receipt.Id, left[next].Id, taken));
                    remaining -= taken;
                    left[next].Left -= taken;
                }

                if (left[next].Left == 0)
                {
                    next++;
                }
            }
        }

        return allocations.GroupBy(a => (a.ReceiptId, a.PaymentId))
            .Select(g => new PaymentAllocation(g.Key.ReceiptId, g.Key.PaymentId, g.Sum(a => a.Amount))).ToList();
    }

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
            .Select(r => Entry(r.ReceiptDate, SupplierStatementEntryKind.Receipt, r.Id, r.Code, r.Total - r.Discount, null))
            .Concat(returns.Select(r => Entry(r.ReturnDate, SupplierStatementEntryKind.Return, r.Id, r.Code, -r.Value, r.ReceiptCode)))
            .Concat(payments.Select(p => new SupplierStatementEntry(
                p.PaymentDate,
                p.Kind == SupplierPaymentKind.Payment ? SupplierStatementEntryKind.Payment : SupplierStatementEntryKind.PaidBack,
                p.Id, p.Code, null, p.Method, p.BankReference, p.Note,
                p.Kind == SupplierPaymentKind.Payment ? -(p.Amount + p.DiscountAmount) : p.Amount, 0)))
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
