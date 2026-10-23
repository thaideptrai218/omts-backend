using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>The checked assignee of a work assignment.</summary>
public sealed record ResolvedAssignee(Employee? Employee, SeasonalWorker? Worker, Contractor? Team)
{
    public string Name => Employee?.FullName ?? Worker?.FullName ?? Team!.Name;
}

/// <summary>LAB-03 lookups: raft and cycle of the zone, the assignee, and the duplicate person–date warning (MSG49).</summary>
public sealed class WorkAssignmentRules(IWorkAssignmentRepository assignments, IWorkforceRepository workforce)
{
    /// <summary>
    /// The assignee; an unchanged assignee of an edit is accepted even if it was discontinued since. A new or changed
    /// employee must be Working in the zone (MSG01 "Employee"), MSG72 first for a Left employee.
    /// </summary>
    public async Task<Result<ResolvedAssignee>> ResolveAssigneeAsync(
        Guid zoneId, Guid? employeeId, Guid? workerId, Guid? teamId, DateOnly workDate, WorkAssignment? current, CancellationToken cancellationToken)
    {
        var unchanged = current is not null && current.EmployeeId == employeeId && current.WorkerId == workerId && current.ContractorId == teamId;
        if (employeeId is { } eid)
        {
            var employee = (await workforce.GetEmployeesAsync([eid], cancellationToken)).FirstOrDefault();
            if (employee is null)
            {
                return AppError.Invalid("Employee");
            }

            var problem = unchanged ? LaborPersonRules.LeftOn(employee, workDate) : LaborPersonRules.CheckForZone(employee, zoneId, workDate);
            return problem is not null ? problem : new ResolvedAssignee(employee, null, null);
        }

        if (workerId is { } wid)
        {
            var worker = (await workforce.GetWorkersAsync([wid], cancellationToken)).FirstOrDefault();
            return worker is null || (!unchanged && worker.StatusId != StatusIds.Usage.InUse)
                ? AppError.Invalid("Worker")
                : new ResolvedAssignee(null, worker, null);
        }

        var team = (await workforce.GetTeamsAsync([teamId!.Value], cancellationToken)).FirstOrDefault();
        return team is null || (!unchanged && team.StatusId != StatusIds.Usage.InUse)
            ? AppError.Invalid("Team")
            : new ResolvedAssignee(null, null, team);
    }

    /// <summary>Raft: In Use raft of the zone; cycle: Open cycle of that raft. Unchanged values of an edit are kept.</summary>
    public async Task<AppError?> CheckRaftAndCycleAsync(Guid zoneId, Guid? raftId, Guid? cycleId, WorkAssignment? current, CancellationToken cancellationToken)
    {
        if (raftId is { } rid)
        {
            var raft = await assignments.GetRaftAsync(rid, cancellationToken);
            if (raft is null || raft.ZoneId != zoneId || (rid != current?.RaftId && raft.StatusId != StatusIds.Usage.InUse))
            {
                return AppError.Invalid("Raft");
            }
        }

        if (cycleId is { } cid && (cid != current?.CycleId || raftId != current?.RaftId))
        {
            var cycle = await assignments.GetCycleAsync(cid, cancellationToken);
            if (cycle is null || cycle.RaftId != raftId || cycle.StatusId != StatusIds.CultivationCycle.Open)
            {
                return AppError.Invalid("Cycle");
            }
        }

        return null;
    }

    /// <summary>MSG49 (409, requiresConfirmation) when the person, or a member of the team, already has work that day.</summary>
    public async Task<AppError?> DuplicateWarningAsync(ResolvedAssignee assignee, DateOnly workDate, Guid? exceptId, CancellationToken cancellationToken)
    {
        List<Guid> employeeIds = [], workerIds = [], teamIds = [];
        if (assignee.Employee is { } employee)
        {
            employeeIds.Add(employee.Id);
        }
        else if (assignee.Worker is { } worker)
        {
            workerIds.Add(worker.Id);
            if (worker.ContractorId is { } ownTeam)
            {
                teamIds.Add(ownTeam);
            }
        }
        else
        {
            teamIds.Add(assignee.Team!.Id);
            workerIds.AddRange((await workforce.GetInUseMembersAsync([assignee.Team.Id], cancellationToken)).Select(m => m.Id));
        }

        var conflicts = await assignments.ListActiveOnDateAsync(workDate, employeeIds, workerIds, teamIds, exceptId, cancellationToken);
        if (conflicts.Count == 0)
        {
            return null;
        }

        var first = conflicts[0];
        var name = first.Employee?.FullName ?? first.Worker?.FullName ?? assignee.Worker?.FullName ?? assignee.Name;
        return AppError.Conflict(
            UserMessages.AlreadyAssigned(name, LaborInputRules.Date(workDate)),
            new Dictionary<string, object?>
            {
                ["requiresConfirmation"] = true,
                ["conflictingAssignmentIds"] = conflicts.Select(c => c.Id).ToList(),
            });
    }
}
