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
    public async Task<AppError?> CheckAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var fromDate = await payrolls.ListEndingOnOrAfterAsync(date, cancellationToken);
        var locked = fromDate.Where(p => p.IsLocked).ToList();
        if (locked.Count > 0)
        {
            return Refusal(date, locked);
        }

        foreach (var open in fromDate)
        {
            unitOfWork.MarkChanged(open);
        }

        return null;
    }

    /// <summary>Names the latest locked payroll, as a date after it is what the user can enter; sending back helps only when none is Finalized.</summary>
    private AppError Refusal(DateOnly date, IReadOnlyList<Payroll> locked)
    {
        var latest = locked.MaxBy(p => p.PeriodEnd)!;
        return AppError.Conflict(
            UserMessages.PayrollLocksDate(
                PayPeriod.NameOf(latest.PeriodEnd),
                statusCatalog.DisplayNameOf(latest.StatusId),
                date,
                latest.PeriodEnd,
                canBeSentBack: locked.All(p => p.StatusId == StatusIds.Payroll.AwaitingFinalization)),
            new Dictionary<string, object?> { ["lockedUntil"] = latest.PeriodEnd });
    }
}
