using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Attendance or work assignments of an employee dated after a day (they block ending the employment).</summary>
public sealed record LaborRecordsAfter(int AttendanceCount, int AssignmentCount)
{
    public int Total => AttendanceCount + AssignmentCount;
}

public interface IEmployeeRepository
{
    Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Employee with working zones (and their zones) and linked account loaded, tracked for editing.</summary>
    Task<Employee?> GetForEditAsync(Guid id, CancellationToken cancellationToken);

    Task<Employee?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Employee id per linked account, for the accounts that have one.</summary>
    Task<IReadOnlyDictionary<Guid, Guid?>> GetIdsByAccountIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>
    /// Employees by name or phone (contains), optional status and working zone, ordered by name, with
    /// working zones and linked account loaded.
    /// </summary>
    Task<(IReadOnlyList<Employee> Items, int Total)> SearchAsync(
        string? search, int? statusId, Guid? zoneId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Another profile of the same person: same full name (trimmed, case-insensitive) and phone.</summary>
    Task<Employee?> FindSamePersonAsync(string fullName, string phoneNumber, Guid? exceptEmployeeId, CancellationToken cancellationToken);

    /// <summary>Attendance rows and not-cancelled work assignments of the employee dated after <paramref name="day"/>.</summary>
    Task<LaborRecordsAfter> CountLaborRecordsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken);

    void Add(Employee employee);

    void AddWorkingZone(EmployeeWorkingZone workingZone);

    void RemoveWorkingZone(EmployeeWorkingZone workingZone);
}
