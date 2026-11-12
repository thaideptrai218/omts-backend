using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>
/// The customer debt of one or more profiles now (BR-DEBT-02): the database sums through <see cref="CustomerDebtMath"/>, so every screen shows the same figure; and which
/// invoices are still unpaid (BR-DEBT-05, <see cref="CustomerDebtAllocator"/>).
/// </summary>
public sealed class CustomerDebtReader(ICustomerDebtQuery query, TimeProvider timeProvider)
{
    public async Task<decimal> DebtAsync(Guid customerId, CancellationToken cancellationToken) =>
        (await DebtsAsync([customerId], cancellationToken))[customerId];

    /// <summary>Every id asked for has an entry.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> DebtsAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var figures = await query.FiguresAsync(ids, cancellationToken);
        return ids.ToDictionary(id => id, id => CustomerDebtMath.Debt(figures.GetValueOrDefault(id, CustomerDebtFigures.None)));
    }

    public async Task<CustomerDebtAllocation> AllocationAsync(Guid customerId, CancellationToken cancellationToken) =>
        (await AllocationsAsync([customerId], cancellationToken))[customerId];

    /// <summary>The allocation of each profile asked for, from two queries whatever their number; every id has an entry.</summary>
    public async Task<IReadOnlyDictionary<Guid, CustomerDebtAllocation>> AllocationsAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, CustomerDebtAllocation>();
        }

        var ledger = await query.LedgerAsync(ids, cancellationToken);
        var invoicesOf = ledger.Invoices.ToLookup(i => i.CustomerId);
        var paymentsOf = ledger.Payments.ToLookup(p => p.CustomerId);
        var adjustmentsOf = ledger.Adjustments.ToLookup(a => a.CustomerId);
        var today = timeProvider.Today();
        return ids.ToDictionary(id => id, id => CustomerDebtAllocator.Allocate(invoicesOf[id], paymentsOf[id], today, adjustmentsOf[id]));
    }
}
