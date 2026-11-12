using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>What is left of one advance of an order, for the plan that sets it against an invoice.</summary>
public sealed record AdvanceLeft(Guid PaymentId, decimal Left);

/// <summary>A part of an advance already set against an invoice, newest first when cut.</summary>
public sealed record AdvancePart(Guid AllocationId, decimal Amount);

/// <summary>The part of an advance set against an invoice.</summary>
public sealed record AdvanceUse(Guid PaymentId, decimal Amount);

/// <summary>
/// The rules of an order that its invoices share, as pure functions so they are unit tested: the share of the order discount an invoice takes, what an order has delivered,
/// how the reserved advance of the order is set against an invoice, and what is cut from the parts when an invoice becomes smaller.
/// </summary>
public static class SalesInvoiceOrderShare
{
    /// <summary>
    /// The discount an invoice of the order takes: the order discount in proportion to the goods of the invoice (rounded half up to the dong), never more than what the earlier invoices left of it.
    /// The seller may change it; what is not shared when the order ends early is dropped.
    /// </summary>
    /// <param name="orderDiscount">The discount of the order.</param>
    /// <param name="orderSubtotal">The goods of the whole order.</param>
    /// <param name="invoiceSubtotal">The goods of this invoice.</param>
    /// <param name="sharedBefore">The discount of the invoices of the order that are not cancelled, other than this one.</param>
    public static decimal DiscountShare(decimal orderDiscount, decimal orderSubtotal, decimal invoiceSubtotal, decimal sharedBefore)
    {
        if (orderDiscount <= 0m || orderSubtotal <= 0m || invoiceSubtotal <= 0m)
        {
            return 0m;
        }

        var proportional = PurchaseMoney.RoundHalfUp(orderDiscount * invoiceSubtotal / orderSubtotal);
        return Math.Min(Math.Max(0m, orderDiscount - sharedBefore), proportional);
    }

    /// <summary>The delivered quantity of each order line once this invoice is counted: what the other invoices delivered plus the lines of this one (by order line, in the unit of the line).</summary>
    public static IReadOnlyDictionary<Guid, decimal> DeliveredWith(IReadOnlyDictionary<Guid, decimal> deliveredByOthers, IEnumerable<(Guid OrderLineId, decimal Quantity)> thisInvoice)
    {
        var total = new Dictionary<Guid, decimal>(deliveredByOthers);
        foreach (var (lineId, quantity) in thisInvoice)
        {
            total[lineId] = total.GetValueOrDefault(lineId) + quantity;
        }

        return total;
    }

    /// <summary>Every line of the order is delivered in full (delivered at least ordered): the order completes by itself.</summary>
    public static bool AllDelivered(IEnumerable<(Guid LineId, decimal Ordered)> orderLines, IReadOnlyDictionary<Guid, decimal> delivered) =>
        orderLines.All(l => delivered.GetValueOrDefault(l.LineId) >= l.Ordered);

    /// <summary>The advances of the order, oldest first, set against an invoice that needs <paramref name="need"/>: each takes what it has left until the invoice is covered.</summary>
    public static IReadOnlyList<AdvanceUse> AdvancePlan(IEnumerable<AdvanceLeft> advancesOldestFirst, decimal need)
    {
        var uses = new List<AdvanceUse>();
        var remaining = need;
        foreach (var advance in advancesOldestFirst)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var take = Math.Min(advance.Left, remaining);
            if (take > 0m)
            {
                uses.Add(new AdvanceUse(advance.PaymentId, take));
                remaining -= take;
            }
        }

        return uses;
    }

    /// <summary>
    /// The parts of advances set against an invoice that stay when the invoice can take only <paramref name="cap"/>: the newest parts are cut first, so the earlier advance is kept.
    /// Returns each part with its new amount (0: the part goes).
    /// </summary>
    public static IReadOnlyList<(AdvancePart Part, decimal NewAmount)> Trim(IEnumerable<AdvancePart> newestFirst, decimal cap)
    {
        var parts = newestFirst.ToList();
        var excess = Math.Max(0m, parts.Sum(p => p.Amount) - Math.Max(0m, cap));
        var result = new List<(AdvancePart, decimal)>();
        foreach (var part in parts)
        {
            var cut = Math.Min(part.Amount, excess);
            excess -= cut;
            result.Add((part, part.Amount - cut));
        }

        return result;
    }
}
