using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// The employee's side of the harvest roster, for the mobile app (like <c>/api/me/purchase-shifts</c>): the employee is the one signed in, never a client-chosen id. It lists the invitations of the employee (without the customer or
/// the cages) and takes the answer, accept or decline. Answering again with the same answer changes nothing; the employee may change their mind while the plan is open (not Completed or Cancelled).
/// </summary>
public sealed class HarvestPlanMyShiftService(
    IHarvestPlanStaffRepository staff,
    IEmployeeRepository employees,
    HarvestPlanNotifier notifier,
    IStatusCatalog statusCatalog,
    ICurrentUser user,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxPeriodDays = 366;

    /// <summary>
    /// The invitations of the employee. Without <paramref name="from"/> and <paramref name="to"/>: those of the plans still open; with them: those of the plans planned in the period (at most 366 days), whatever their status.
    /// 400 MSG-GEN-01 <c>Period</c> for a wrong period; 403 for an account that is not an employee.
    /// </summary>
    public async Task<Result<IReadOnlyList<MyHarvestShift>>> MineAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        if ((from is null) != (to is null) || (from is { } f && to is { } t && (t < f || t.DayNumber - f.DayNumber > MaxPeriodDays)))
        {
            return AppError.Invalid("Period");
        }

        var employee = await CurrentEmployeeAsync(cancellationToken);
        if (employee is null)
        {
            return AppError.Forbidden;
        }

        var rows = await staff.ListOfEmployeeAsync(employee.Id, from, to, cancellationToken);
        return Result<IReadOnlyList<MyHarvestShift>>.Success(rows.Select(ViewOf).ToList());
    }

    /// <summary>
    /// The employee accepts (<c>accept: true</c>, Confirmed) or declines (Declined, the reason is optional). 403 for a row of another employee; 404 for a row that does not exist; 400 MSG-GEN-01 <c>Accept</c> or <c>Reason</c>;
    /// 409 MSG-GEN-09 once the plan is Completed or Cancelled, or for a person added outside the system. The Owners (and the Zone Managers of a plan they harvest) are told.
    /// </summary>
    public async Task<Result<MyHarvestShift>> RespondAsync(Guid rowId, RespondHarvestShiftRequest request, CancellationToken cancellationToken)
    {
        var employee = await CurrentEmployeeAsync(cancellationToken);
        if (employee is null)
        {
            return AppError.Forbidden;
        }

        var row = await staff.GetForEditAsync(rowId, cancellationToken);
        if (row is null)
        {
            return AppError.NotFound;
        }

        if (row.EmployeeId != employee.Id)
        {
            return AppError.Forbidden;
        }

        var reason = request.Reason?.Trim();
        if (request.Accept is null)
        {
            return AppError.InvalidFields(["Accept"]);
        }

        if (reason is { Length: > HarvestPlanRosterRules.MaxReasonLength })
        {
            return AppError.InvalidFields(["Reason"]);
        }

        var plan = row.Plan;
        if (!HarvestPlanRosterRules.Open(plan.StatusId))
        {
            return AppError.Conflict(UserMessages.PlanStaffingClosed(plan.Code, statusCatalog.DisplayNameOf(plan.StatusId)));
        }

        if (!HarvestPlanRosterRules.MayRespond(plan.StatusId, row.State))
        {
            return AppError.Conflict(UserMessages.WrongStatus("Harvest plan staff", row.State.ToString()));
        }

        var accept = request.Accept.Value;
        // The same answer again changes nothing, tells nobody and is answered as the first time; a manual confirmation is kept when the employee accepts.
        var already = accept
            ? row.State is HarvestPlanStaffState.Confirmed or HarvestPlanStaffState.ConfirmedManually
            : row.State == HarvestPlanStaffState.Declined;
        if (already)
        {
            return ViewOf(row);
        }

        var before = row.State;
        row.State = accept ? HarvestPlanStaffState.Confirmed : HarvestPlanStaffState.Declined;
        row.Reason = accept || string.IsNullOrEmpty(reason) ? null : reason;
        row.RespondedAt = timeProvider.GetUtcNow();
        row.ConfirmedByAccountId = null;
        audit.Record(new AuditEntry("HarvestPlan.StaffResponded", HarvestPlanService.RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            Before = new { Employee = employee.FullName, State = before.ToString() },
            After = new { Employee = employee.FullName, State = row.State.ToString() },
            Reason = row.Reason,
        });
        await notifier.StaffRespondedAsync(plan, employee.FullName, accept, row.Reason, HarvestPlanRosterRules.Summary(plan), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ViewOf(row);
    }

    private async Task<Domain.Entities.Identity.Employee?> CurrentEmployeeAsync(CancellationToken cancellationToken) =>
        user.AccountId is { } accountId ? await employees.GetByAccountIdAsync(accountId, cancellationToken) : null;

    private MyHarvestShift ViewOf(HarvestPlanStaffMember row)
    {
        var plan = row.Plan;
        return new MyHarvestShift(
            row.Id,
            plan.Id,
            plan.Code,
            plan.StatusId,
            statusCatalog.DisplayNameOf(plan.StatusId),
            plan.PlannedAt,
            plan.Executor,
            plan.Lines.OrderBy(l => l.LineNumber).Select(l => new MyHarvestShiftRaft(l.Raft.Code, l.Raft.Zone.Code)).ToList(),
            row.State,
            row.InvitedAt,
            row.RespondedAt,
            row.Reason,
            HarvestPlanRosterRules.MayRespond(plan.StatusId, row.State),
            HarvestPlanRosterRules.Summary(plan),
            RowVersionGuard.Encode(row.RowVersion));
    }
}
