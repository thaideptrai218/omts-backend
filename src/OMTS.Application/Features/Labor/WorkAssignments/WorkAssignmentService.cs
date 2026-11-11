using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <summary>
/// LABOR-02.0 assign work: a job type and a raft for a date, for several employees. An employee assigned to another raft and
/// not checked in yet is moved after a confirmation (LABOR-02.4, MSG-LABOR-06); one already checked in is refused
/// (MSG-LABOR-07, Change job for crew moves them). The request is all or nothing. The raft's zone joins the working zones of each
/// employee who lacks it (BR-LABOR-01), and the employees are pushed after the save (MSG-LABOR-05).
/// </summary>
public sealed class WorkAssignmentService(
    IWorkAssignmentRepository assignments,
    WorkAssignmentWorkingZones workingZones,
    WorkAssignmentGuard guard,
    WorkAssignmentViews views,
    ZoneAccessGuard zoneAccess,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<WorkAssignmentsSaved>> AssignAsync(Guid raftId, AssignWorkRequest request, CancellationToken cancellationToken)
    {
        var (raft, raftRefused) = await guard.RaftAsync(raftId, PermissionAction.Create, "Raft", fromRoute: true, cancellationToken);
        if (raft is null)
        {
            return raftRefused!;
        }

        if (await guard.DateRefusalAsync(request.Date, cancellationToken) is { } dateRefused)
        {
            return dateRefused;
        }

        var date = request.Date!.Value;
        var (jobType, jobTypeRefused) = await guard.JobTypeAsync(request.JobTypeId, cancellationToken);
        var (ids, idsRefused) = WorkAssignmentGuard.EmployeeIds(request.EmployeeIds);
        if (jobType is null || ids is null)
        {
            return jobTypeRefused ?? idsRefused!;
        }

        var found = await assignments.GetEmployeesAsync(ids, cancellationToken);
        if (found.Count != ids.Count)
        {
            return AppError.Invalid("Employees");
        }

        var selected = ids.Select(id => found.First(e => e.Id == id)).ToList();
        if (selected.Select(e => guard.EmployeeRefusal(e, date)).FirstOrDefault(r => r is not null) is { } employeeRefused)
        {
            return employeeRefused;
        }

        var existing = await assignments.ListOfEmployeesOnAsync(ids, date, tracked: true, cancellationToken);
        var attendance = await guard.AttendanceAsync(date, ids, cancellationToken);
        var checkedIn = selected
            .Where(e => (attendance.TryGetValue(e.Id, out var day) && day.IsCheckedIn) || existing.Any(p => p.EmployeeId == e.Id && !p.IsFirstPeriod))
            .ToList();
        if (checkedIn.Count > 0)
        {
            return WorkAssignmentGuard.AlreadyCheckedIn(checkedIn);
        }

        // In the order of the request, so the question names the first employee the Zone Manager selected.
        var moves = selected.Select(e => existing.FirstOrDefault(p => p.EmployeeId == e.Id && p.RaftId != raft.Id)).OfType<WorkAssignment>().ToList();
        foreach (var move in moves)
        {
            // Taking an employee off the crew of a raft of a zone the caller does not work in is that zone's Zone Manager's call.
            if (!await zoneAccess.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Edit, move.Raft.ZoneId, cancellationToken))
            {
                return AppError.Forbidden;
            }
        }

        if (moves.Count > 0 && !request.MoveConfirmed)
        {
            return MoveQuestion(moves, raft, date);
        }

        var by = currentUser.AccountId ?? throw new InvalidOperationException("Work is assigned by a logged-in user.");
        var saved = new List<WorkAssignment>();
        var notices = new List<WorkAssignmentNotice>();
        foreach (var employee in selected)
        {
            var current = existing.FirstOrDefault(p => p.EmployeeId == employee.Id);
            if (current is null)
            {
                current = new WorkAssignment
                {
                    EmployeeId = employee.Id, Employee = employee, WorkDate = date, RaftId = raft.Id, Raft = raft,
                    JobTypeId = jobType.Id, JobType = jobType, AssignedByAccountId = by,
                };
                assignments.Add(current);
                audit.Record(Entry("WorkAssignment.Created", current) with { After = WorkAssignmentViews.Snapshot(current) });
            }
            else if (current.RaftId != raft.Id || current.JobTypeId != jobType.Id)
            {
                var before = WorkAssignmentViews.Snapshot(current);
                var action = current.RaftId != raft.Id ? "WorkAssignment.Moved" : "WorkAssignment.Updated";
                (current.RaftId, current.Raft, current.JobTypeId, current.JobType, current.AssignedByAccountId) = (raft.Id, raft, jobType.Id, jobType, by);
                audit.Record(Entry(action, current) with { Before = before, After = WorkAssignmentViews.Snapshot(current) });
            }
            else
            {
                saved.Add(current);
                continue;
            }

            workingZones.Ensure(employee, raft);
            saved.Add(current);
            notices.Add(WorkAssignmentViews.Assigned(current));
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel save assigned the same employee on the same date (filtered unique index): one assignment per employee and date.
            return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
        }

        await views.NotifyAsync(notices, cancellationToken);
        var message = UserMessages.Saved("Work assignments");
        return new WorkAssignmentsSaved(saved.Select(views.View).ToList(), message.Text, message.Code);
    }

    /// <summary>MSG-LABOR-06 for the first employee; <c>moves</c> lists every employee and raft, and <c>moveConfirmed</c> is the answer to send.</summary>
    private static AppError MoveQuestion(IReadOnlyList<WorkAssignment> moves, Raft raft, DateOnly date) => AppError.Conflict(
        UserMessages.MoveToRaft(moves[0].Employee.FullName, moves[0].Raft.Code, date, raft.Code),
        new Dictionary<string, object?>
        {
            ["confirmationField"] = "moveConfirmed",
            ["moves"] = moves.Select(m => new { employeeId = m.EmployeeId, employeeName = m.Employee.FullName, raftCode = m.Raft.Code, newRaftCode = raft.Code }).ToList(),
        });

    private static AuditEntry Entry(string action, WorkAssignment a) =>
        new(action, nameof(WorkAssignment)) { RecordId = a.Id, RecordCode = a.Raft.Code, ZoneId = a.Raft.ZoneId };
}
