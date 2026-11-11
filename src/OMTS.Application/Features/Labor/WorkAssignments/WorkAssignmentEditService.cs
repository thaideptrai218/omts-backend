using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <summary>
/// LABOR-02.2 edit or remove the work assigned to one employee: until the employee is checked in, for today or a later date
/// (BR-LABOR-03). Both the old and the new raft must be in the caller's zones. Each save moves the row version of the
/// assignment, so a stale screen or a parallel save answers MSG06; the employee is pushed after the save (MSG-LABOR-05).
/// </summary>
public sealed class WorkAssignmentEditService(
    IWorkAssignmentRepository assignments,
    WorkAssignmentGuard guard,
    WorkAssignmentViews views,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<WorkAssignmentSaved>> EditAsync(Guid id, EditWorkAssignmentRequest request, CancellationToken cancellationToken)
    {
        var (assignment, refused) = await EditableAsync(id, PermissionAction.Edit, request.RowVersion, cancellationToken);
        if (assignment is null)
        {
            return refused!;
        }

        if (request.RaftId is null && request.JobTypeId is null)
        {
            return AppError.Invalid("Raft or job type");
        }

        Raft? raft = assignment.Raft;
        if (request.RaftId is { } raftId && raftId != assignment.RaftId)
        {
            (raft, var raftRefused) = await guard.RaftAsync(raftId, PermissionAction.Edit, "Raft", fromRoute: false, cancellationToken);
            if (raft is null)
            {
                return raftRefused!;
            }
        }

        JobType? jobType = assignment.JobType;
        if (request.JobTypeId is { } jobTypeId && jobTypeId != assignment.JobTypeId)
        {
            (jobType, var jobTypeRefused) = await guard.JobTypeAsync(jobTypeId, cancellationToken);
            if (jobType is null)
            {
                return jobTypeRefused!;
            }
        }

        if (raft.Id != assignment.RaftId || jobType.Id != assignment.JobTypeId)
        {
            var before = WorkAssignmentViews.Snapshot(assignment);
            var action = raft.Id != assignment.RaftId ? "WorkAssignment.Moved" : "WorkAssignment.Updated";
            (assignment.RaftId, assignment.Raft, assignment.JobTypeId, assignment.JobType) = (raft.Id, raft, jobType.Id, jobType);
            audit.Record(Entry(action, assignment) with { Before = before, After = WorkAssignmentViews.Snapshot(assignment) });
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await views.NotifyAsync([WorkAssignmentViews.Assigned(assignment)], cancellationToken);
        }

        var saved = UserMessages.Saved("Work assignment");
        return new WorkAssignmentSaved(views.View(assignment), saved.Text, saved.Code);
    }

    /// <summary>Removes the assignment; the audit log keeps it (LABOR-02.2). Ask MSG-GEN-04 first.</summary>
    public async Task<Result<Done>> RemoveAsync(Guid id, string? rowVersion, CancellationToken cancellationToken)
    {
        var (assignment, refused) = await EditableAsync(id, PermissionAction.Delete, rowVersion, cancellationToken);
        if (assignment is null)
        {
            return refused!;
        }

        assignments.Remove(assignment);
        audit.Record(Entry("WorkAssignment.Removed", assignment) with { Before = WorkAssignmentViews.Snapshot(assignment) });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await views.NotifyAsync([WorkAssignmentViews.Removed(assignment)], cancellationToken);
        return Done.Value;
    }

    /// <summary>
    /// The assignment when the caller may change it now: in the zone of its raft, not stale, for today or later, work assigned
    /// before the check-in (a job period starts at one), the employee Employed and not checked in, and no locked pay period.
    /// </summary>
    private async Task<(WorkAssignment? Assignment, AppError? Error)> EditableAsync(
        Guid id, PermissionAction action, string? rowVersion, CancellationToken cancellationToken)
    {
        if (await assignments.GetForEditAsync(id, cancellationToken) is not { } assignment)
        {
            return (null, AppError.NotFound);
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.WorkAssignments, action, assignment.Raft.ZoneId, cancellationToken))
        {
            return (null, AppError.Forbidden);
        }

        if (await rowVersions.CheckAsync(assignment, rowVersion, cancellationToken) is { } stale)
        {
            return (null, stale);
        }

        if (await guard.DateRefusalAsync(assignment.WorkDate, cancellationToken) is { } dateRefused)
        {
            return (null, dateRefused);
        }

        var employee = assignment.Employee;
        if (employee.StatusId != StatusIds.Employee.Working)
        {
            return (null, AppError.Conflict(UserMessages.WrongStatus($"Employee {employee.FullName}", statusCatalog.DisplayNameOf(employee.StatusId))));
        }

        var attendance = await guard.AttendanceAsync(assignment.WorkDate, [employee.Id], cancellationToken);
        return !assignment.IsFirstPeriod || attendance.TryGetValue(employee.Id, out var day) && day.IsCheckedIn
            ? (null, WorkAssignmentGuard.AlreadyCheckedIn([employee]))
            : (assignment, null);
    }

    private static AuditEntry Entry(string action, WorkAssignment a) =>
        new(action, nameof(WorkAssignment)) { RecordId = a.Id, RecordCode = a.Raft.Code, ZoneId = a.Raft.ZoneId };
}
