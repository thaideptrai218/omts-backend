using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// Period lock shared by LAB-03/04/05/07 (set by LAB-08): a date in a month whose payroll is finalized answers
/// 409 PERIOD_LOCKED. Reopening the payroll unlocks the month.
/// </summary>
public sealed class PayrollPeriodLock(IPayrollRepository payrolls, IUnitOfWork unitOfWork)
{
    /// <summary>
    /// Null when every date is in an open month. A draft payroll of such a month is marked changed in the caller's
    /// save, so a finalize that commits in between makes the save fail (MSG06) instead of slipping a record into a
    /// locked month, and a save that commits first makes the finalize retry with that record included.
    /// </summary>
    public async Task<AppError?> CheckAsync(IEnumerable<DateOnly> dates, CancellationToken cancellationToken)
    {
        var list = dates.Distinct().ToList();
        var periods = list.Select(d => PayrollPeriod.Of(d).Key).Distinct().ToList();
        var month = await payrolls.ListByPeriodsAsync(periods, cancellationToken);
        var finalized = month.Where(p => p.IsFinalized).Select(p => p.Period).ToHashSet();
        foreach (var draft in month.Where(p => !p.IsFinalized))
        {
            unitOfWork.MarkChanged(draft);
        }

        return PayrollPeriodLockRule.FirstLocked(list, finalized) is { } locked ? Locked(locked) : null;
    }

    public Task<AppError?> CheckAsync(DateOnly date, CancellationToken cancellationToken) => CheckAsync([date], cancellationToken);

    public static AppError Locked(PayrollPeriod period) =>
        AppError.Conflict(UserMessages.PeriodLocked(period.Label), new Dictionary<string, object?> { ["period"] = period.Key });
}
