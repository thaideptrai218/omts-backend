using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor;

/// <summary>
/// BR-LABOR-13: while a payroll is Awaiting finalization, and for good once it is Finalized, nothing dated on or before its
/// last day changes (MSG-LABOR-04: "the payroll of that pay period, or of a later one"). The check runs before a dated change
/// is put on the unit of work. A payroll that can still be sent or finalized is touched by that same save (PR #13's pattern):
/// whichever of the change and the send comes second meets a new row version and answers MSG06, so neither is lost on the
/// other. A payroll created and sent between this read and the save is not seen; LABOR-12 takes that window when it sends.
/// </summary>
public sealed class PayPeriodLock(IPayrollRepository payrolls, IStatusCatalog statusCatalog, IUnitOfWork unitOfWork)
{
    /// <summary>MSG-LABOR-04 when <paramref name="date"/> is on or before the last locked day; otherwise null.</summary>
    public async Task<AppError?> CheckAsync(DateOnly date, CancellationToken cancellationToken) =>
        RefuseOrTouch(await payrolls.ListEndingOnOrAfterAsync(date, cancellationToken), date, null);

    /// <summary>
    /// For a new type that becomes, or replaces, the first of an employee. A day before every row has the first row's type, so
    /// the save moves every earlier day as well, and every payroll holds a moved day or ends on or after <paramref name="date"/>:
    /// any locked one refuses. This is the conservative reading of BR-LABOR-13; the PM may relax it (design Q11).
    /// </summary>
    /// <param name="firstTypeDate">The date of the first row now: a date after it moves no earlier day.</param>
    public async Task<AppError?> CheckFirstTypeChangeAsync(DateOnly date, DateOnly firstTypeDate, CancellationToken cancellationToken) =>
        RefuseOrTouch(await payrolls.ListEndingOnOrAfterAsync(DateOnly.MinValue, cancellationToken), date, firstTypeDate);

    private AppError? RefuseOrTouch(IReadOnlyList<Payroll> affected, DateOnly date, DateOnly? firstTypeDate)
    {
        var locked = affected.Where(p => p.IsLocked).ToList();
        if (locked.Count > 0)
        {
            return Refusal(date, firstTypeDate, locked);
        }

        foreach (var open in affected)
        {
            unitOfWork.MarkChanged(open);
        }

        return null;
    }

    /// <summary>
    /// Names the latest locked payroll, as a date after it is what the user can enter; sending back helps only when none is
    /// Finalized. A date outside the payroll means the change reaches its last day through the first-row rule, so that day is
    /// the one named, and the date to choose is also after the first type's.
    /// </summary>
    private AppError Refusal(DateOnly date, DateOnly? firstTypeDate, IReadOnlyList<Payroll> locked)
    {
        var latest = locked.MaxBy(p => p.PeriodEnd)!;
        var dateAfter = firstTypeDate > latest.PeriodEnd ? firstTypeDate.Value : latest.PeriodEnd;
        return AppError.Conflict(
            UserMessages.PayrollLocksDate(
                PayPeriod.NameOf(latest.PeriodEnd),
                statusCatalog.DisplayNameOf(latest.StatusId),
                date <= latest.PeriodEnd ? date : latest.PeriodEnd,
                dateAfter,
                canBeSentBack: locked.All(p => p.StatusId == StatusIds.Payroll.AwaitingFinalization)),
            new Dictionary<string, object?> { ["lockedUntil"] = dateAfter });
    }
}
