using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>LABOR-02: the work assignments, one row per job period of an employee and day (BR-LABOR-03).</summary>
public interface IWorkAssignmentRepository
{
    /// <summary>A job period with its raft (and zone), job type and employee, tracked.</summary>
    Task<WorkAssignment?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every job period of the day, with employee, raft, zone and job type, untracked, by employee name then start.</summary>
    Task<IReadOnlyList<WorkAssignment>> ListDayAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The job periods of these employees on the day, with raft, zone and job type, tracked, by start (the first period first).</summary>
    Task<IReadOnlyList<WorkAssignment>> ListOfEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The job periods of one employee from <paramref name="from"/> to <paramref name="to"/>, with raft, zone and job type, untracked.</summary>
    Task<IReadOnlyList<WorkAssignment>> ListOfEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>The job types with these ids, untracked.</summary>
    Task<IReadOnlyList<JobType>> GetJobTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The first job period of the employee on the day, if any (the one that holds the work assigned for it).</summary>
    Task<WorkAssignment?> FindFirstAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken);

    void Add(WorkAssignment assignment);

    void Remove(WorkAssignment assignment);
}

/// <summary>LABOR-04: one attendance record per employee and day (BR-LABOR-06).</summary>
public interface IAttendanceRepository
{
    /// <summary>The record of the employee on the day, tracked.</summary>
    Task<Attendance?> GetAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The records of these employees on the day, tracked.</summary>
    Task<IReadOnlyList<Attendance>> ListOfEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Every record of the day, untracked.</summary>
    Task<IReadOnlyList<Attendance>> ListDayAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The records of one employee from <paramref name="from"/> to <paramref name="to"/>, untracked.</summary>
    Task<IReadOnlyList<Attendance>> ListOfEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    void Add(Attendance attendance);
}

/// <summary>LABOR-10, LABOR-05: the requests employees send to the Cooperative Owner.</summary>
public interface IEmployeeRequestRepository
{
    /// <summary>The request with its employee, tracked.</summary>
    Task<EmployeeRequest?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The request of the employee sent with this client operation id, if any.</summary>
    Task<EmployeeRequest?> FindByClientOperationAsync(Guid employeeId, Guid clientOperationId, CancellationToken cancellationToken);

    /// <summary>A request of the employee Awaiting review of this type whose days overlap <paramref name="from"/>…<paramref name="to"/>, if any.</summary>
    Task<EmployeeRequest?> FindAwaitingAsync(Guid employeeId, Domain.Enums.EmployeeRequestType type, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>
    /// The requests with their employee, untracked: Awaiting review first, oldest first, then the others, newest first. A null
    /// <paramref name="statusId"/> lists every status.
    /// </summary>
    Task<IReadOnlyList<EmployeeRequest>> ListAsync(int? statusId, int take, CancellationToken cancellationToken);

    /// <summary>The requests of one employee whose days overlap the range, or that are still Awaiting review, untracked, newest first.</summary>
    Task<IReadOnlyList<EmployeeRequest>> ListOfEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>The approved leave that covers <paramref name="day"/>, for these employees, untracked.</summary>
    Task<IReadOnlyList<EmployeeRequest>> ListApprovedLeaveAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken);

    void Add(EmployeeRequest request);
}

/// <summary>What the work screens read about employees (LABOR-02, LABOR-04): the Employed ones and their rows.</summary>
public interface IWorkEmployeeReader
{
    /// <summary>The employees with these ids, with working zones, type periods and job types, tracked (a working zone may be added).</summary>
    Task<IReadOnlyList<Employee>> GetForWorkAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// The Employed employees whose last working day is not before <paramref name="day"/> and who have a working zone among
    /// <paramref name="zoneIds"/> (every one when null), with working zones, type periods and job types, untracked, by name.
    /// </summary>
    Task<IReadOnlyList<Employee>> ListEmployedAsync(IReadOnlyCollection<Guid>? zoneIds, DateOnly day, CancellationToken cancellationToken);
}
