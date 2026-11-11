using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>LABOR-02: the work assignments (job periods) of employees, and the rafts and employees a Zone Manager picks from.</summary>
public interface IWorkAssignmentRepository
{
    /// <summary>
    /// The raft with its zone, and the job type without its rates, both tracked: an assignment that is saved with them shares
    /// their instances with the assignments read tracked in the same request, which a detached copy would clash with.
    /// </summary>
    Task<Raft?> GetRaftAsync(Guid raftId, CancellationToken cancellationToken);

    Task<JobType?> GetJobTypeAsync(Guid jobTypeId, CancellationToken cancellationToken);

    /// <summary>The assignment with its employee, raft (and zone) and job type, tracked.</summary>
    Task<WorkAssignment?> GetForEditAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The job periods of these employees on the date, with employee, raft (and zone) and job type, the first period of each
    /// employee first. Tracked when they are going to change.
    /// </summary>
    Task<IReadOnlyList<WorkAssignment>> ListOfEmployeesOnAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly date, bool tracked, CancellationToken cancellationToken);

    /// <summary>
    /// Every job period of the date of each employee who has one on a raft of the permitted zones (the periods on rafts of other
    /// zones included, so the caller can see where a period ends), untracked, with employee, raft (and zone) and job type.
    /// </summary>
    Task<IReadOnlyList<WorkAssignment>> ListOnAsync(DateOnly date, PermittedZones zones, CancellationToken cancellationToken);

    /// <summary>The rafts In Use of the permitted zones with their zones, by zone code and raft code.</summary>
    Task<IReadOnlyList<Raft>> ListRaftsInUseAsync(PermittedZones zones, CancellationToken cancellationToken);

    /// <summary>The employees with these ids, whatever their status, with their working zones, tracked.</summary>
    Task<IReadOnlyList<Employee>> GetEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken);

    /// <summary>
    /// The Employed employees, untracked, with working zones, job types and type periods, by name; those who have the job type
    /// first. <paramref name="zones"/> limits them to a working zone among them (null: the whole cooperative); the search text
    /// matches the name or the phone number (<paramref name="phoneNumber"/> is the text as a whole stored number, if it is one).
    /// </summary>
    Task<IReadOnlyList<Employee>> ListEmployedAsync(
        PermittedZones? zones, string? search, string? phoneNumber, Guid? jobTypeFirst, int limit, CancellationToken cancellationToken);

    void Add(WorkAssignment assignment);

    void Remove(WorkAssignment assignment);
}
