using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>What a smaller invoice takes back of the advance of its order: each part cut and its new amount (0: the part goes), and the money that returns to the advance.</summary>
/// <param name="ReservedGain">What returns to the advance while the order is open: the customer's money stays held for the order, so it is not free credit that could be owed back.</param>
internal sealed record AdvanceTrim(IReadOnlyList<(CustomerPaymentAllocation Allocation, decimal NewAmount)> Cuts, decimal Trimmed, decimal ReservedGain)
{
    public static readonly AdvanceTrim None = new([], 0m, 0m);
}

/// <summary>
/// An invoice of an order that becomes smaller (an edit, a price cut) cannot keep more of the advance of the order than it now needs: its newest parts are cut, so the earlier advance is kept
/// (<see cref="SalesInvoiceOrderShare.Trim"/>), after the cash the invoice was paid with, which is never cut. What is cut is the advance's again.
/// </summary>
public sealed class SalesInvoiceAdvanceTrimmer(ISalesInvoiceRepository invoices, ISalesOrderRepository orders, IUnitOfWork unitOfWork)
{
    internal async Task<AdvanceTrim> PlanAsync(SalesInvoice invoice, decimal newTotal, CancellationToken cancellationToken)
    {
        if (invoice.SalesOrderId is not { } orderId)
        {
            return AdvanceTrim.None;
        }

        var parts = await invoices.AdvanceAllocationsOfAsync(invoice.Id, orderId, cancellationToken);
        if (parts.Count == 0)
        {
            return AdvanceTrim.None;
        }

        var cap = newTotal - SalesInvoiceChangeMoney.PaidOf(invoice.Payments);
        var byId = parts.ToDictionary(p => p.Id);
        var cuts = SalesInvoiceOrderShare.Trim(parts.Select(p => new AdvancePart(p.Id, p.Amount)), cap)
            .Where(t => t.NewAmount != t.Part.Amount)
            .Select(t => (byId[t.Part.AllocationId], t.NewAmount))
            .ToList();
        var trimmed = cuts.Sum(c => c.Item1.Amount - c.NewAmount);
        var open = await orders.GetForEditAsync(orderId, cancellationToken) is { } order && SalesOrderRules.IsOpen(order.StatusId);
        return new AdvanceTrim(cuts, trimmed, open ? trimmed : 0m);
    }

    internal void Apply(AdvanceTrim trim)
    {
        foreach (var (allocation, newAmount) in trim.Cuts)
        {
            // Read for what is left of it, so a parallel invoice of the order and this save cannot both win.
            unitOfWork.MarkChanged(allocation.Payment);
            if (newAmount <= 0m)
            {
                invoices.RemoveAllocation(allocation);
            }
            else
            {
                allocation.Amount = newAmount;
            }
        }
    }
}
