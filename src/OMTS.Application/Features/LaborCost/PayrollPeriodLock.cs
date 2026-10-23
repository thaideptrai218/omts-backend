using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// Period lock shared by LAB-03/04/05/07 (set by LAB-08): a date in a month whose payroll is finalized answers
/// 409 PERIOD_LOCKED. Reopening the payroll unlocks the month.
/// </summary>
public sealed class PayrollPeriodLock(IPayrollRepository payrolls)
{
    /// <summary>Null when every date is in an open month.</summary>
    public async Task<AppError?> CheckAsync(IEnumerable<DateOnly> dates, CancellationToken cancellationToken)
    {
        var list = dates.Distinct().ToList();
        var periods = list.Select(d => PayrollPeriod.Of(d).Key).Distinct().ToList();
        var finalized = await payrolls.GetFinalizedPeriodsAsync(periods, cancellationToken);
        return PayrollPeriodLockRule.FirstLocked(list, finalized) is { } locked ? Locked(locked) : null;
    }

    public Task<AppError?> CheckAsync(DateOnly date, CancellationToken cancellationToken) => CheckAsync([date], cancellationToken);

    public static AppError Locked(PayrollPeriod period) =>
        AppError.Conflict(UserMessages.PeriodLocked(period.Label), new Dictionary<string, object?> { ["period"] = period.Key });
}
