namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// Period lock (LAB-08): once the payroll of a month is finalized, assignments, attendance and piecework
/// confirmations dated in that month cannot be created, edited or cancelled. Pure rule; the database lookup is
/// in <see cref="PayrollPeriodLock"/>.
/// </summary>
public static class PayrollPeriodLockRule
{
    /// <summary>The first locked month among the dates, or null when every date is open.</summary>
    public static PayrollPeriod? FirstLocked(IEnumerable<DateOnly> dates, IReadOnlySet<string> finalizedPeriods)
    {
        foreach (var period in dates.Select(PayrollPeriod.Of).Distinct().OrderBy(p => p.Start))
        {
            if (finalizedPeriods.Contains(period.Key))
            {
                return period;
            }
        }

        return null;
    }
}
