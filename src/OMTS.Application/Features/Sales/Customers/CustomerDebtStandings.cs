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
        var ages = await debts.AgesAsync(debtOf.Where(d => d.Value > 0m).ToDictionary(d => d.Key, d => d.Value), cancellationToken);
        return ids.ToDictionary(id => id, id => policy.Evaluate(debtOf[id], ages.GetValueOrDefault(id)?.DebtAgeDays));
    }

    /// <summary>
    /// The dashboard's due debts: how many customers owe more than 0, how many are over the limit (amount or days) and how many reach the days limit within <paramref name="soonDays"/> days.
    /// The last two are 0 while the warning is off (the invoices have no due date of their own). <paramref name="debtOf"/> is the debt of every profile, read once by the caller
    /// (<see cref="CustomerDebtService.DebtOfEveryProfileAsync"/>).
    /// </summary>
    public async Task<(int Debtors, int Overdue, int DueSoon)> DueSummaryAsync(
        IReadOnlyDictionary<Guid, decimal> debtOf, CustomerDebtPolicy policy, int soonDays, CancellationToken cancellationToken)
    {
        var owing = debtOf.Where(d => d.Value > 0m).ToDictionary(d => d.Key, d => d.Value);
        if (!policy.Enabled || owing.Count == 0)
        {
            return (owing.Count, 0, 0);
        }

        // A debt above the amount limit is overdue whatever its age, so only the others need one.
        var ages = await AgesOfAsync(owing.Where(d => !policy.Evaluate(d.Value, null).OverAmount).ToDictionary(d => d.Key, d => d.Value), policy, cancellationToken);
        var overdue = 0;
        var dueSoon = 0;
        foreach (var (id, debt) in owing)
        {
            var days = ages.GetValueOrDefault(id)?.DebtAgeDays;
            if (policy.Evaluate(debt, days).Over)
            {
                overdue++;
            }
            else if (policy.MaxDays is { } max && days is { } age && age >= max - soonDays)
            {
                dueSoon++;
            }
        }

        return (owing.Count, overdue, dueSoon);
    }

    /// <summary>The ids of every customer over the limit; empty while the warning is off.</summary>
    public async Task<IReadOnlySet<Guid>> OverLimitIdsAsync(CustomerDebtPolicy policy, CancellationToken cancellationToken)
    {
        if (!policy.Enabled)
        {
            return new HashSet<Guid>();
        }

        var debtOf = (await debtQuery.AllFiguresAsync(cancellationToken)).ToDictionary(f => f.Key, f => CustomerDebtMath.Debt(f.Value));
        var owing = debtOf.Where(d => d.Value > 0m).ToDictionary(d => d.Key, d => d.Value);
        var overAmount = owing.Where(d => policy.Evaluate(d.Value, null).OverAmount).Select(d => d.Key).ToHashSet();
        var ages = await AgesOfAsync(owing.Where(d => !overAmount.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value), policy, cancellationToken);
        return owing.Keys.Where(id => overAmount.Contains(id) || policy.Evaluate(owing[id], ages.GetValueOrDefault(id)?.DebtAgeDays).Over).ToHashSet();
    }

    /// <summary>The ages of the debts, read only when the days limit is set: nothing else looks at them.</summary>
    private async Task<IReadOnlyDictionary<Guid, CustomerDebtAge>> AgesOfAsync(IReadOnlyDictionary<Guid, decimal> owing, CustomerDebtPolicy policy, CancellationToken cancellationToken) =>
        policy.MaxDays is null ? new Dictionary<Guid, CustomerDebtAge>() : await debts.AgesAsync(owing, cancellationToken);
}
