using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>
/// LAB-03 – the Zone Manager assigns daily-wage work or piecework to an employee, a seasonal worker or a whole team for
/// a date in one of their zones. Editable until the month is finalized; cancelled (with a reason) instead of deleted.
/// </summary>
public sealed class WorkAssignmentService(
    IWorkAssignmentRepository assignments,
    WorkAssignmentRules rules,
    WorkAssignmentViews views,
    WorkAssignmentNotifier notifier,
    PayrollPeriodLock periodLock,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const string Entity = "Work assignment";

    public async Task<Result<WorkAssignmentView>> CreateAsync(Guid zoneId, SaveWorkAssignmentRequest request, CancellationToken cancellationToken)
    {
        var zone = await assignments.GetZoneAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid("Zone");
        }

        var checkedRequest = await CheckAsync(zoneId, request, null, cancellationToken);
        if (!checkedRequest.IsSuccess)
        {
            return checkedRequest.Error!;
        }

        var (input, assignee) = checkedRequest.Value;
        var assignment = new WorkAssignment { ZoneId = zoneId };
        Apply(assignment, input, assignee, request);
        assignments.Add(assignment);
        audit.Record(new AuditEntry("WorkAssignment.Created", nameof(WorkAssignment))
        {
            RecordId = assignment.Id, ZoneId = zoneId, After = WorkAssignmentViews.Snapshot(assignment),
        });
        await notifier.NotifyAsync(assignee.Employee, assignment, zone.Name, cancelled: false, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(assignment, zone.Name, cancellationToken);
    }

    public async Task<Result<WorkAssignmentView>> UpdateAsync(Guid zoneId, Guid id, SaveWorkAssignmentRequest request, CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetAsync(id, cancellationToken);
        if (assignment is null || assignment.ZoneId != zoneId)
        {
            return AppError.NotFound;
        }

        if (assignment.IsCancelled)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(Entity, WorkAssignmentViews.Cancelled, WorkAssignmentViews.Active, "the assignment has been cancelled"));
        }

        if (await rowVersions.CheckAsync(assignment, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var checkedRequest = await CheckAsync(zoneId, request, assignment, cancellationToken);
        if (!checkedRequest.IsSuccess)
        {
            return checkedRequest.Error!;
        }

        var (input, assignee) = checkedRequest.Value;
        var before = WorkAssignmentViews.Snapshot(assignment);
        var (oldEmployee, oldDate) = (assignment.Employee, assignment.WorkDate);
        Apply(assignment, input, assignee, request);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, WorkAssignmentViews.Snapshot(assignment));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("WorkAssignment.Updated", nameof(WorkAssignment))
            {
                RecordId = assignment.Id, ZoneId = zoneId, Before = changedBefore, After = changedAfter,
            });
        }

        if (oldEmployee?.Id != assignee.Employee?.Id)
        {
            await notifier.NotifyAsync(oldEmployee, assignment, assignment.Zone.Name, cancelled: true, cancellationToken);
            await notifier.NotifyAsync(assignee.Employee, assignment, assignment.Zone.Name, cancelled: false, cancellationToken);
        }
        else if (oldDate != assignment.WorkDate)
        {
            await notifier.NotifyAsync(assignee.Employee, assignment, assignment.Zone.Name, cancelled: false, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(assignment, assignment.Zone.Name, cancellationToken);
    }

    /// <summary>Active → Cancelled with a reason; never after the piecework completion was confirmed (MSG61).</summary>
    public async Task<Result<WorkAssignmentView>> CancelAsync(Guid zoneId, Guid id, CancelWorkAssignmentRequest request, CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetAsync(id, cancellationToken);
        if (assignment is null || assignment.ZoneId != zoneId)
        {
            return AppError.NotFound;
        }

        var reasonInvalid = new List<string>();
        var reason = LaborInputRules.Required(request.Reason, LaborInputRules.ReasonLength, "Reason", reasonInvalid);
        if (reason is null)
        {
            return AppError.InvalidFields(reasonInvalid);
        }

        if (assignment.IsCancelled)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(Entity, WorkAssignmentViews.Cancelled, WorkAssignmentViews.Cancelled, "the assignment is already cancelled"));
        }

        if (assignment.IsCompleted)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(Entity, WorkAssignmentViews.Completed, WorkAssignmentViews.Cancelled, "completion has already been confirmed"));
        }

        if (await periodLock.CheckAsync(assignment.WorkDate, cancellationToken) is { } locked)
        {
            return locked;
        }

        (assignment.CancelledAt, assignment.CancelledByAccountId, assignment.CancellationReason) = (timeProvider.GetUtcNow(), currentUser.AccountId, reason);
        audit.Record(new AuditEntry("WorkAssignment.Cancelled", nameof(WorkAssignment))
        {
            RecordId = assignment.Id, ZoneId = zoneId, Reason = reason,
            Before = new { State = WorkAssignmentViews.Active }, After = new { State = WorkAssignmentViews.Cancelled },
        });
        await notifier.NotifyAsync(assignment.Employee, assignment, assignment.Zone.Name, cancelled: true, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(assignment, assignment.Zone.Name, cancellationToken);
    }

    /// <summary>Field rules, period lock, completed-piecework restrictions, raft/cycle, assignee and the MSG49 warning.</summary>
    private async Task<Result<(WorkAssignmentInput Input, ResolvedAssignee Assignee)>> CheckAsync(
        Guid zoneId, SaveWorkAssignmentRequest request, WorkAssignment? current, CancellationToken cancellationToken)
    {
        var (input, invalid) = WorkAssignmentInput.From(request);
        if (input is null)
        {
            return AppError.InvalidFields(invalid);
        }

        var dates = current is null ? [input.WorkDate] : new[] { current.WorkDate, input.WorkDate };
        if (await periodLock.CheckAsync(dates, cancellationToken) is { } locked)
        {
            return locked;
        }

        if (current is { IsCompleted: true } && input.FieldLockedAfterCompletion(current, request) is { } field)
        {
            return AppError.Invalid(field, "Completion has been confirmed: only Task and Notes can change.");
        }

        if (await rules.CheckRaftAndCycleAsync(zoneId, request.RaftId, request.CycleId, current, cancellationToken) is { } raftError)
        {
            return raftError;
        }

        var assignee = await rules.ResolveAssigneeAsync(zoneId, request.EmployeeId, request.WorkerId, request.TeamId, input.WorkDate, current, cancellationToken);
        if (!assignee.IsSuccess)
        {
            return assignee.Error!;
        }

        var rescheduled = current is null || current.WorkDate != input.WorkDate
            || current.EmployeeId != request.EmployeeId || current.WorkerId != request.WorkerId || current.ContractorId != request.TeamId;
        if (rescheduled && !request.ConfirmDuplicate
            && await rules.DuplicateWarningAsync(assignee.Value!, input.WorkDate, current?.Id, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return (input, assignee.Value!);
    }

    private static void Apply(WorkAssignment a, WorkAssignmentInput input, ResolvedAssignee assignee, SaveWorkAssignmentRequest request) =>
        input.ApplyTo(a, assignee.Employee, assignee.Worker, assignee.Team, request.RaftId, request.CycleId);
}
