using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

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
    /// Employees matching the filter, with their rows loaded, one page. Without a sort, those with a working zone among
    /// <paramref name="firstZoneIds"/> come first, then by name; with one, only that order counts. Always ending by id.
    /// </summary>
    Task<(IReadOnlyList<Employee> Items, int Total)> SearchAsync(
        EmployeeSearchFilter filter, IReadOnlyCollection<Guid>? firstZoneIds, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The same employees and order as <see cref="SearchAsync"/>, no paging: at most <paramref name="limit"/> + 1 rows (one more than allowed tells a caller the result is too long).</summary>
    Task<IReadOnlyList<Employee>> ListAsync(EmployeeSearchFilter filter, IReadOnlyCollection<Guid>? firstZoneIds, int limit, CancellationToken cancellationToken);

    /// <summary>Another employee of the cooperative with this code, whatever its letter case.</summary>
    Task<Employee?> FindByCodeAsync(string code, Guid? exceptEmployeeId, CancellationToken cancellationToken);

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

/// <summary>How the employee list is ordered; <see cref="Default"/> is the zone-first name order.</summary>
public enum EmployeeSort
{
    Default,
    Name,
    Code,
    Phone,
    Type,
    Status,
    LastWorkingDay,
}

/// <summary>The employee list filters; null means "any".</summary>
/// <param name="Search">Name, code or phone (contains).</param>
/// <param name="PhoneNumber">The search read as a whole phone number in its stored form, if it is one: it also finds that number.</param>
/// <param name="Today">The day the employee type in force is read at.</param>
/// <param name="Ids">Only these employees (the export of selected rows); the filters above are then left empty by the caller.</param>
public sealed record EmployeeSearchFilter(
    string? Search,
    string? PhoneNumber,
    int? StatusId,
    Guid? ZoneId,
    EmployeeType? Type,
    Guid? JobTypeId,
    DateOnly Today,
    EmployeeSort Sort = EmployeeSort.Default,
    bool Descending = false,
    IReadOnlyCollection<Guid>? Ids = null);
