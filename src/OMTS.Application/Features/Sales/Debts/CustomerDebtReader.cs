using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>How old the debt of a profile is now (BR-DEBT-05), as the list screens and the debt limit read it.</summary>
/// <param name="OldestUnpaidDate">The date the age counts from; null when there is no debt apart from the reserved advances.</param>
/// <param name="DebtAgeDays">Days from <paramref name="OldestUnpaidDate"/> to today.</param>
/// <param name="ReservedTotal">The advances reserved for open orders (<see cref="CustomerDebtAllocation.ReservedTotal"/>).</param>
public sealed record CustomerDebtAge(decimal Debt, DateOnly? OldestUnpaidDate, int? DebtAgeDays, decimal ReservedTotal);

/// <summary>
/// The customer debt of one or more profiles now (BR-DEBT-02): the database sums through <see cref="CustomerDebtMath"/>, so every screen shows the same figure; and which
/// invoices are still unpaid (BR-DEBT-05, <see cref="CustomerDebtAllocator"/>).
/// <see cref="AllocationsAsync"/> runs the allocator over the whole ledger of each profile: the answer the writes (payments, cancellations, corrections) are checked against.
/// The read screens (<see cref="AgesAsync"/>, <see cref="UnpaidAmountsAsync"/>, <see cref="SnapshotAsync"/>) get the same answer from the database without reading the ledger, except for
/// a profile that holds an advance reserved for an open order: what such an advance leaves reserved depends on the order the payments were recorded in, so its ledger is allocated here.
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

    /// <summary>
    /// The age of the debt of each profile in <paramref name="debtOf"/>, whose debts the caller has read already. Only a profile that owes (debt above 0) has an age to look for, and one
    /// that holds a reserved advance is allocated in full; every key has an entry.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, CustomerDebtAge>> AgesAsync(IReadOnlyDictionary<Guid, decimal> debtOf, CancellationToken cancellationToken)
    {
        if (debtOf.Count == 0)
        {
            return new Dictionary<Guid, CustomerDebtAge>();
        }

        var today = timeProvider.Today();
        var reserved = await query.CustomersWithReservedAdvanceAsync(debtOf.Keys.ToList(), cancellationToken);
        var allocated = await AllocationsAsync(reserved, cancellationToken);
        var owing = debtOf.Where(d => d.Value > 0m && !reserved.Contains(d.Key)).Select(d => d.Key).ToList();
        var oldest = owing.Count == 0
            ? new Dictionary<Guid, DateOnly>()
            : (await query.UnpaidInvoicesAsync(owing, oldestOnly: true, cancellationToken)).ToDictionary(r => r.CustomerId, r => r.InvoiceDate);
        var withoutInvoice = owing.Where(id => !oldest.ContainsKey(id)).ToList();
        var origins = withoutInvoice.Count == 0 ? new Dictionary<Guid, DateOnly>() : await query.DebtOriginDatesAsync(withoutInvoice, cancellationToken);
        return debtOf.ToDictionary(d => d.Key, d =>
        {
            if (allocated.TryGetValue(d.Key, out var allocation))
            {
                return new CustomerDebtAge(d.Value, allocation.OldestUnpaidDate, allocation.DebtAgeDays, allocation.ReservedTotal);
            }

            DateOnly? date = oldest.TryGetValue(d.Key, out var first) ? first : origins.TryGetValue(d.Key, out var origin) ? origin : null;
            return new CustomerDebtAge(d.Value, date, date is { } day ? today.DayNumber - day.DayNumber : null, 0m);
        });
    }

    /// <summary>What is still unpaid of each invoice of the profiles that has something unpaid; an invoice with no entry is covered.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> UnpaidAmountsAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var reserved = await query.CustomersWithReservedAdvanceAsync(ids, cancellationToken);
        var plain = ids.Where(id => !reserved.Contains(id)).ToList();
        var unpaid = plain.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await query.UnpaidInvoicesAsync(plain, oldestOnly: false, cancellationToken)).ToDictionary(r => r.InvoiceId, r => r.Unpaid);
        foreach (var allocation in (await AllocationsAsync(reserved, cancellationToken)).Values)
        {
            foreach (var invoice in allocation.UnpaidInvoices)
            {
                unpaid[invoice.Id] = invoice.Unpaid;
            }
        }

        return unpaid;
    }

    /// <summary>
    /// The allocation of one profile for a screen that only reads it (the statement, the debt tab): the same answer as <see cref="AllocationAsync"/>, without reading the ledger of a
    /// profile that holds no reserved advance.
    /// </summary>
    public async Task<CustomerDebtAllocation> SnapshotAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if ((await query.CustomersWithReservedAdvanceAsync([customerId], cancellationToken)).Contains(customerId))
        {
            return await AllocationAsync(customerId, cancellationToken);
        }

        var today = timeProvider.Today();
        var debt = await DebtAsync(customerId, cancellationToken);
        var unpaid = (await query.UnpaidInvoicesAsync([customerId], oldestOnly: false, cancellationToken))
            .Select(r => new UnpaidInvoice(r.InvoiceId, r.Code, r.InvoiceDate, r.Total, r.Total - r.Unpaid, r.Unpaid, today.DayNumber - r.InvoiceDate.DayNumber))
            .ToList();
        // A debt with no unpaid invoice comes only from money paid back above the payments, or an adjustment: it is as old as that entry.
        DateOnly? oldest = debt <= 0m
            ? null
            : unpaid.Count > 0
                ? unpaid[0].InvoiceDate
                : (await query.DebtOriginDatesAsync([customerId], cancellationToken)).TryGetValue(customerId, out var origin) ? origin : null;
        return new CustomerDebtAllocation(debt, unpaid, oldest, oldest is { } date ? today.DayNumber - date.DayNumber : null, []);
    }
}
