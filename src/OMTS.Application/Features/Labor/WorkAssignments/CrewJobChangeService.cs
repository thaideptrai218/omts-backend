using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <summary>
/// LABOR-02.1 Change job for crew, today only: each selected member who is checked in and not checked out gets a new job
/// period that starts now, with the new job type and, when the crew moves, the new raft; each who is not checked in yet has the work
/// assignment changed instead (BR-LABOR-03, BR-LABOR-05). Members who keep their job are left as they are. A job change sends no
/// push, because the crew is told on site (BR-LABOR-04).
/// </summary>
public sealed class CrewJobChangeService(
    IWorkAssignmentRepository assignments,
    WorkAssignmentGuard guard,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<CrewJobChanged>> ChangeAsync(Guid raftId, CrewJobChangeRequest request, CancellationToken cancellationToken)
    {
        var (raft, raftRefused) = await guard.RaftAsync(raftId, PermissionAction.Edit, "Raft", fromRoute: true, cancellationToken);
        if (raft is null)
        {
            return raftRefused!;
        }

        var (newRaft, newRaftRefused) = request.NewRaftId is { } other && other != raftId
            ? await guard.RaftAsync(other, PermissionAction.Edit, "New raft", fromRoute: false, cancellationToken)
            : (raft, null);
        var (jobType, jobTypeRefused) = await guard.JobTypeAsync(request.JobTypeId, cancellationToken);
        var (ids, idsRefused) = WorkAssignmentGuard.EmployeeIds(request.EmployeeIds);
        if (newRaft is null || jobType is null || ids is null)
        {
            return newRaftRefused ?? jobTypeRefused ?? idsRefused!;
        }

        var today = guard.Today();
        if (await guard.DateRefusalAsync(today, cancellationToken) is { } locked)
        {
            return locked;
        }

        var periods = await assignments.ListOfEmployeesOnAsync(ids, today, tracked: true, cancellationToken);
        var attendance = await guard.AttendanceAsync(today, ids, cancellationToken);
        var crew = new List<(WorkAssignment Current, WorkAssignment First, DayAttendance? Day)>();
        foreach (var id in ids)
        {
            var own = periods.Where(p => p.EmployeeId == id).ToList();
            var current = WorkAssignmentGuard.CurrentPeriod(own);
            attendance.TryGetValue(id, out var day);
            // A member of this crew has their latest job period on this raft today and has not checked out (LABOR-02.1 step 2).
            if (current is null || current.RaftId != raft.Id || day?.CheckOutAt is not null)
            {
                return AppError.Invalid("Employees");
            }

            crew.Add((current, own.FirstOrDefault(p => p.IsFirstPeriod) ?? current, day));
        }

        if (!crew.Any(m => m.Day is { IsAtWork: true }))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Crew of raft {raft.Code}", "Not checked in"));
        }

        var now = timeProvider.GetUtcNow();
        var by = currentUser.AccountId ?? throw new InvalidOperationException("The job is changed by a logged-in user.");
        var changes = new List<CrewMemberChange>();
        foreach (var (current, first, day) in crew)
        {
            var name = current.Employee.FullName;
            if (current.JobTypeId == jobType.Id && current.RaftId == newRaft.Id)
            {
                changes.Add(new CrewMemberChange(current.EmployeeId, name, CrewChangeResults.Unchanged, current.Id));
            }
            else if (day is { IsAtWork: true })
            {
                var period = new WorkAssignment
                {
                    EmployeeId = current.EmployeeId, Employee = current.Employee, WorkDate = today, RaftId = newRaft.Id, Raft = newRaft,
                    JobTypeId = jobType.Id, JobType = jobType, StartTime = now, AssignedByAccountId = by,
                };
                if (current.StartTime >= now)
                {
                    return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
                }

                assignments.Add(period);
                audit.Record(Entry("WorkAssignment.JobPeriodAdded", period) with { Before = WorkAssignmentViews.Snapshot(current), After = WorkAssignmentViews.Snapshot(period) });
                changes.Add(new CrewMemberChange(current.EmployeeId, name, CrewChangeResults.PeriodAdded, period.Id));
            }
            else
            {
                var before = WorkAssignmentViews.Snapshot(first);
                (first.RaftId, first.Raft, first.JobTypeId, first.JobType, first.AssignedByAccountId) = (newRaft.Id, newRaft, jobType.Id, jobType, by);
                audit.Record(Entry("WorkAssignment.Updated", first) with { Before = before, After = WorkAssignmentViews.Snapshot(first) });
                changes.Add(new CrewMemberChange(first.EmployeeId, name, CrewChangeResults.AssignmentChanged, first.Id));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved("Job change");
        return new CrewJobChanged(changes, saved.Text, saved.Code);
    }

    private static AuditEntry Entry(string action, WorkAssignment a) =>
        new(action, nameof(WorkAssignment)) { RecordId = a.Id, RecordCode = a.Raft.Code, ZoneId = a.Raft.ZoneId };
}
