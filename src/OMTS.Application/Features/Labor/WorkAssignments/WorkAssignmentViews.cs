using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Messages;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <summary>The view of a saved assignment and the push to the employee about it (MSG-LABOR-05); shared by the services of LABOR-02.</summary>
public sealed class WorkAssignmentViews(IEmployeeWorkNotifier notifier, ILogger<WorkAssignmentViews> logger)
{
    /// <summary>The assignment with employee, raft (and zone) and job type loaded.</summary>
    public WorkAssignmentView View(WorkAssignment a) => new(
        a.Id, a.EmployeeId, a.Employee.FullName, a.WorkDate, a.RaftId, a.Raft.Code, a.Raft.ZoneId, a.JobTypeId, a.JobType.Name, a.StartTime,
        RowVersionGuard.Encode(a.RowVersion));

    /// <summary>The audit snapshot of an assignment: what the Zone Manager chose.</summary>
    public static object Snapshot(WorkAssignment a) =>
        new { a.EmployeeId, Employee = a.Employee.FullName, Date = a.WorkDate, a.RaftId, Raft = a.Raft.Code, a.JobTypeId, JobType = a.JobType.Name, a.StartTime };

    public static string WorkText(WorkAssignment a) => $"{a.JobType.Name} on raft {a.Raft.Code}";

    public static WorkAssignmentNotice Assigned(WorkAssignment a) =>
        new(a.EmployeeId, a.WorkDate, UserMessages.WorkAssignmentNotice(a.WorkDate, WorkText(a)));

    public static WorkAssignmentNotice Removed(WorkAssignment a) =>
        new(a.EmployeeId, a.WorkDate, UserMessages.WorkAssignmentNotice(a.WorkDate, $"the work {WorkText(a)} is removed"));

    /// <summary>Pushes after the save. A push that fails changes nothing and is not retried (GFR-06); the log keeps the employee ids only.</summary>
    public async Task NotifyAsync(IReadOnlyList<WorkAssignmentNotice> notices, CancellationToken cancellationToken)
    {
        if (notices.Count == 0)
        {
            return;
        }

        try
        {
            await notifier.NotifyAsync(notices, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The work assignment push to {Count} employee(s) failed; the assignments stay saved.", notices.Count);
        }
    }
}
