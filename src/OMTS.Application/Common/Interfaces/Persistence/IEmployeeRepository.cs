using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IEmployeeRepository
{
    Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Employee with working zones, job types, type periods and linked account loaded, tracked for editing.</summary>
    Task<Employee?> GetForEditAsync(Guid id, CancellationToken cancellationToken);

    Task<Employee?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Employee id per linked account, for the accounts that have one.</summary>
    Task<IReadOnlyDictionary<Guid, Guid?>> GetIdsByAccountIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>
    /// Employees by name or phone (contains), optional status and working zone, with their rows loaded. Those with a working
    /// zone among <paramref name="firstZoneIds"/> come first, then by name.
    /// </summary>
    /// <param name="search">The text as typed.</param>
    /// <param name="phoneNumber">The text read as a whole phone number in its stored form, if it is one: it also finds that number.</param>
    Task<(IReadOnlyList<Employee> Items, int Total)> SearchAsync(
        string? search, string? phoneNumber, int? statusId, Guid? zoneId, IReadOnlyCollection<Guid>? firstZoneIds, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The Employed employee who has this number (at most one, BR-LABOR-01), other than <paramref name="exceptEmployeeId"/>.</summary>
    Task<Employee?> FindEmployedWithPhoneAsync(string phoneNumber, Guid? exceptEmployeeId, CancellationToken cancellationToken);

    /// <summary>The job types that exist among <paramref name="ids"/>.</summary>
    Task<IReadOnlyList<JobType>> GetJobTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Employees still Employed whose last working day is before <paramref name="day"/>.</summary>
    Task<IReadOnlyList<Guid>> ListEmployedWithLastWorkingDayBeforeAsync(DateOnly day, CancellationToken cancellationToken);

    void Add(Employee employee);

    void AddWorkingZone(EmployeeWorkingZone workingZone);

    void RemoveWorkingZone(EmployeeWorkingZone workingZone);

    void AddJobType(EmployeeJobType jobType);

    void RemoveJobType(EmployeeJobType jobType);

    void AddTypePeriod(EmployeeTypePeriod period);
}
