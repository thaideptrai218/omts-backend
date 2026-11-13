using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>The debt limit of the cooperative as the checks read it.</summary>
public sealed record CustomerDebtPolicy(bool Enabled, decimal? MaxAmount, int? MaxDays, bool AllowMoreOverLimit)
{
    public static CustomerDebtPolicy From(Cooperative? cooperative) => cooperative is null
        ? new CustomerDebtPolicy(false, null, null, true)
        : new CustomerDebtPolicy(
            cooperative.CustomerDebtWarningEnabled, cooperative.CustomerMaxDebtAmount, cooperative.CustomerMaxDebtDays, cooperative.CustomerAllowMoreDebtOverLimit);

    /// <summary>The debt is above the amount limit, or has stood for more days than the days limit. Always false while the warning is off.</summary>
    public CustomerDebtStanding Evaluate(decimal debt, int? debtDays) => new(
        Enabled && MaxAmount is { } amount && debt > amount,
        Enabled && MaxDays is { } days && debt > 0m && debtDays > days,
        debt > 0m ? debtDays : null);
}

/// <summary>Where a customer stands against the debt limit now.</summary>
/// <param name="OverAmount">The debt is above the amount limit.</param>
/// <param name="OverDays">The debt has stood longer than the days limit.</param>
/// <param name="DebtDays">Days since the oldest unpaid debt invoice; null without a debt.</param>
public sealed record CustomerDebtStanding(bool OverAmount, bool OverDays, int? DebtDays)
{
    public static readonly CustomerDebtStanding None = new(false, false, null);

    public bool Over => OverAmount || OverDays;
}

/// <summary>The debt limit of the cooperative applied to customer profiles, from the same debt and age every other screen shows (<see cref="CustomerDebtReader"/>).</summary>
public sealed class CustomerDebtStandings(ICooperativeRepository cooperatives, CustomerDebtReader debts, ICustomerDebtQuery debtQuery)
{
    public async Task<CustomerDebtPolicy> PolicyAsync(CancellationToken cancellationToken) =>
        CustomerDebtPolicy.From(await cooperatives.GetCurrentAsync(cancellationToken));

    /// <summary>Every id asked for has an entry. The age is worked out only for the customers who owe.</summary>
    public async Task<IReadOnlyDictionary<Guid, CustomerDebtStanding>> ForAsync(
        IReadOnlyCollection<Guid> customerIds, CustomerDebtPolicy policy, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, CustomerDebtStanding>();
        }

        var debtOf = await debts.DebtsAsync(ids, cancellationToken);
        var allocations = await debts.AllocationsAsync(ids.Where(id => debtOf[id] > 0m).ToList(), cancellationToken);
        return ids.ToDictionary(id => id, id => policy.Evaluate(debtOf[id], allocations.GetValueOrDefault(id)?.DebtAgeDays));
    }

    /// <summary>The ids of every customer over the limit; empty while the warning is off.</summary>
    public async Task<IReadOnlySet<Guid>> OverLimitIdsAsync(CustomerDebtPolicy policy, CancellationToken cancellationToken)
    {
        if (!policy.Enabled)
        {
            return new HashSet<Guid>();
        }

        var debtOf = (await debtQuery.AllFiguresAsync(cancellationToken)).ToDictionary(f => f.Key, f => CustomerDebtMath.Debt(f.Value));
        var owing = debtOf.Where(d => d.Value > 0m).Select(d => d.Key).ToList();
        var allocations = policy.MaxDays is null
            ? new Dictionary<Guid, CustomerDebtAllocation>()
            : (await debts.AllocationsAsync(owing, cancellationToken)).ToDictionary(a => a.Key, a => a.Value);
        return owing
            .Where(id => policy.Evaluate(debtOf[id], allocations.GetValueOrDefault(id)?.DebtAgeDays).Over)
            .ToHashSet();
    }
}
