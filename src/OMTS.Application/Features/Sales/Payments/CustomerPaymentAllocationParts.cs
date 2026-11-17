using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Debts;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// The parts a payment sets against the unpaid invoices of its customer (BR-DEBT-05), as pure functions over the debt reading of the profile: the one rule of the payments the
/// Accountant records, of a VietQR code that waits for its transfer, and of a transfer without a customer attached to a profile (DEBT-02.6).
/// </summary>
public static class CustomerPaymentAllocationParts
{
    /// <summary>
    /// The parts of the payment: the ones sent (each an invoice of the customer with that much left to pay), or, when none are sent and <paramref name="oldestFirstWhenNone"/>, the
    /// unpaid invoices oldest first up to <paramref name="covers"/>; none otherwise. The shape of the parts (whole VND above zero, one per invoice, together at most what the
    /// payment covers) is checked first by <c>CustomerPaymentRules.InvalidAllocations</c>.
    /// </summary>
    /// <param name="covers">The money of the payment and its discount.</param>
    public static IReadOnlyList<(Guid InvoiceId, decimal Amount)> Of(
        IReadOnlyList<CustomerPaymentAllocationInput>? sent, decimal covers, CustomerDebtAllocation reading, bool oldestFirstWhenNone, out AppError? error)
    {
        error = null;
        var unpaid = reading.UnpaidInvoices.ToDictionary(u => u.Id);
        if (sent is not null)
        {
            var parts = new List<(Guid InvoiceId, decimal Amount)>();
            for (var i = 0; i < sent.Count; i++)
            {
                var field = $"Allocation {i + 1}";
                if (!unpaid.TryGetValue(sent[i].InvoiceId!.Value, out var invoice))
                {
                    error = AppError.Invalid(field, "Not an invoice of this customer with money left to pay.");
                    return [];
                }

                if (sent[i].Amount > invoice.Unpaid)
                {
                    error = AppError.Invalid(field, $"Invoice {invoice.Code} has {invoice.Unpaid:0} VND left to pay.");
                    return [];
                }

                parts.Add((invoice.Id, sent[i].Amount!.Value));
            }

            return parts;
        }

        if (!oldestFirstWhenNone)
        {
            return [];
        }

        var oldestFirst = new List<(Guid InvoiceId, decimal Amount)>();
        var remaining = covers;
        foreach (var invoice in reading.UnpaidInvoices)
        {
            if (remaining <= 0)
            {
                break;
            }

            var part = Math.Min(remaining, invoice.Unpaid);
            oldestFirst.Add((invoice.Id, part));
            remaining -= part;
        }

        return oldestFirst;
    }

    /// <summary>
    /// The reading of a profile once a payment of <paramref name="amount"/> with these parts is on it: the debt lower by the amount, each invoice it pays with that much less to pay (the
    /// others as they were). A second payment of the same profile in one list reads it, so it cannot pay what the first one paid.
    /// </summary>
    public static CustomerDebtAllocation After(CustomerDebtAllocation reading, IReadOnlyList<(Guid InvoiceId, decimal Amount)> parts, decimal amount)
    {
        var paid = parts.ToDictionary(p => p.InvoiceId, p => p.Amount);
        var unpaid = reading.UnpaidInvoices
            .Select(u => paid.TryGetValue(u.Id, out var part) ? u with { Covered = u.Covered + part, Unpaid = u.Unpaid - part } : u)
            .Where(u => u.Unpaid > 0)
            .ToList();
        return reading with { Debt = reading.Debt - amount, UnpaidInvoices = unpaid };
    }
}
