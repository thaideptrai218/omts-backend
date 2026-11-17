using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Workforce;

/// <summary>Why the work assignment of one crew member was not made. A short code the client words: see <see cref="CrewSkipReasons"/>.</summary>
public sealed record CrewAssignmentSkip(Guid EmployeeId, string Reason);

/// <summary>The answer of the automatic work assignment that follows a record with a crew: how many were assigned and who was left out, with the reason.</summary>
public sealed record CrewAssignmentResult(int Created, IReadOnlyList<CrewAssignmentSkip> Skipped);

public static class CrewSkipReasons
{
    /// <summary>The record is not of today: work is assigned for today or later only.</summary>
    public const string PastDate = "PastDate";

    /// <summary>The employee already has work that day; it is never moved without asking.</summary>
    public const string AlreadyAssigned = "AlreadyAssigned";

    /// <summary>The caller may not create work assignments in the zone.</summary>
    public const string NoPermission = "NoPermission";

    /// <summary>The job type (Ươm, Treo giống, Thu hoạch) is not in the catalogue or is not In Use.</summary>
    public const string JobTypeNotInUse = "JobTypeNotInUse";

    /// <summary>The employee is no longer employed today.</summary>
    public const string Left = "Left";

    /// <summary>The assignment was refused for another reason (a locked pay period, a raft no longer In Use) or lost a race.</summary>
    public const string Failed = "Failed";
}

/// <summary>The job types the automatic assignment uses, by their names in the catalogue (decision of 17/11/2026).</summary>
public static class RecordCrewJobs
{
    public const string Nursery = "Ươm";
    public const string Seeding = "Treo giống";
    public const string Harvest = "Thu hoạch";
}

/// <summary>
/// Decision of 17/11/2026: when a nursery, seeding or harvest record is saved with employees, they get a work assignment for the day of the record, with the job type of the record and its place (the zone
/// of a nursery batch, the raft of a seeding session or a harvest lot). It runs after the record is saved, as a step of its own, so the record stays saved whatever happens here. The assignment is made only
/// for today, never moves an employee who already has work that day, and only for a caller who may create work assignments in the zone. It creates no attendance.
/// </summary>
public sealed class RecordCrewAssignmentService(
    WorkAssignmentService work,
    IWorkAssignmentRepository assignments,
    IWorkEmployeeReader employeeReader,
    IJobTypeRepository jobTypes,
    ZoneAccessGuard zones,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <param name="recordDate">The date of the record (the start date of a batch, the seeding date, the harvest date).</param>
    /// <param name="zoneId">The zone of the record.</param>
    /// <param name="raftId">The raft of the record; null for a nursery batch, which is in a zone.</param>
    /// <param name="jobName">One of <see cref="RecordCrewJobs"/>.</param>
    /// <param name="crew">The employees to assign: those newly named on the record.</param>
    public async Task<CrewAssignmentResult> AssignAsync(
        DateOnly recordDate, Guid zoneId, Guid? raftId, string jobName, IReadOnlyList<Employee> crew, CancellationToken cancellationToken)
    {
        var skipped = new List<CrewAssignmentSkip>();
        if (crew.Count == 0)
        {
            return new CrewAssignmentResult(0, skipped);
        }

        void SkipAll(IEnumerable<Employee> employees, string reason) => skipped.AddRange(employees.Select(e => new CrewAssignmentSkip(e.Id, reason)));

        var today = timeProvider.Today();
        if (recordDate != today)
        {
            SkipAll(crew, CrewSkipReasons.PastDate);
            return new CrewAssignmentResult(0, skipped);
        }

        if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Create, zoneId, cancellationToken))
        {
            SkipAll(crew, CrewSkipReasons.NoPermission);
            return new CrewAssignmentResult(0, skipped);
        }

        var job = await jobTypes.FindByNameAsync(jobName, null, cancellationToken);
        if (job is null || job.StatusId != StatusIds.Usage.InUse)
        {
            SkipAll(crew, CrewSkipReasons.JobTypeNotInUse);
            return new CrewAssignmentResult(0, skipped);
        }

        var ids = crew.Select(e => e.Id).ToList();
        var people = (await employeeReader.GetForWorkAsync(ids, cancellationToken)).ToDictionary(e => e.Id);
        var working = (await assignments.ListOfEmployeesAsync(ids, today, cancellationToken)).Select(a => a.EmployeeId).ToHashSet();
        var eligible = new List<Guid>();
        foreach (var employee in crew)
        {
            if (!people.TryGetValue(employee.Id, out var person) || !WorkRules.IsEmployedOn(person, today))
            {
                skipped.Add(new CrewAssignmentSkip(employee.Id, CrewSkipReasons.Left));
            }
            else if (working.Contains(employee.Id))
            {
                skipped.Add(new CrewAssignmentSkip(employee.Id, CrewSkipReasons.AlreadyAssigned));
            }
            else
            {
                eligible.Add(employee.Id);
            }
        }

        if (eligible.Count == 0)
        {
            return new CrewAssignmentResult(0, skipped);
        }

        var failure = await TryAssignAsync(new AssignWorkRequest(raftId, job.Id, today, eligible, MoveExisting: false, ZoneId: raftId is null ? zoneId : null), cancellationToken);
        if (failure is null)
        {
            return new CrewAssignmentResult(eligible.Count, skipped);
        }

        skipped.AddRange(eligible.Select(id => new CrewAssignmentSkip(id, failure)));
        return new CrewAssignmentResult(0, skipped);
    }

    /// <summary>All or none, as the work assignment is. Null when it was made, else the reason code; a lost race or a refusal never reaches the caller as an error, as the record is already saved.</summary>
    private async Task<string?> TryAssignAsync(AssignWorkRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var assigned = await work.AssignAsync(request, cancellationToken);
            if (assigned.IsSuccess)
            {
                return null;
            }

            unitOfWork.DiscardChanges();
            return assigned.Error!.Kind switch
            {
                ErrorKind.Forbidden => CrewSkipReasons.NoPermission,
                ErrorKind.Conflict => CrewSkipReasons.AlreadyAssigned,
                _ => CrewSkipReasons.Failed,
            };
        }
        catch (Exception exception) when (exception is ConcurrencyConflictException or DuplicateRecordException or ForeignKeyViolationException)
        {
            unitOfWork.DiscardChanges();
            return exception is DuplicateRecordException ? CrewSkipReasons.AlreadyAssigned : CrewSkipReasons.Failed;
        }
    }
}
