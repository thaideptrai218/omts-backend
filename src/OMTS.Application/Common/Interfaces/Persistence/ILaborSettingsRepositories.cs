using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>How the job type list is ordered.</summary>
public enum JobTypeSort
{
    Name,
    Rate,
    EffectiveFrom,
    Status,
}

/// <summary>LABOR-11: job types with their rates.</summary>
public interface IJobTypeRepository
{
    /// <summary>The job type with its rates, tracked.</summary>
    Task<JobType?> GetForEditAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Another job type with the name, whatever its letter case (BR-LABOR-12).</summary>
    Task<JobType?> FindByNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken);

    /// <summary>One page of the job types by name or status, or by the rate in force on <paramref name="today"/> or the date it applies from (the job types with no rate in force first when ascending); always ending by id.</summary>
    Task<(IReadOnlyList<JobType> Items, int Total)> SearchAsync(
        string? search, int? statusId, JobTypeSort sort, bool descending, DateOnly today, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The job types that can be chosen for profiles and work (GBR-01), by name.</summary>
    Task<IReadOnlyList<JobType>> ListInUseAsync(CancellationToken cancellationToken);

    void Add(JobType jobType);

    void AddRate(PayRate rate);
}

/// <summary>The salary setup of each employee, from its date (tab "Thiết lập lương").</summary>
public interface IEmployeeSalaryRepository
{
    /// <summary>The employee with its type periods and salary setups, tracked.</summary>
    Task<Employee?> GetWithSalarySetupsAsync(Guid employeeId, CancellationToken cancellationToken);

    /// <summary>The salary type in force on <paramref name="day"/> of each employee that has a setup by then; employees without one are left out.</summary>
    Task<IReadOnlyDictionary<Guid, SalaryType>> TypesOnAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken);

    void Add(EmployeeSalarySetup setup);
}

/// <summary>LABOR-15: the history of the attendance settings.</summary>
public interface IAttendanceSettingsRepository
{
    /// <summary>Every version, the latest date first, tracked.</summary>
    Task<IReadOnlyList<AttendanceSettingsVersion>> ListAsync(CancellationToken cancellationToken);

    void Add(AttendanceSettingsVersion version);
}

/// <summary>The payroll headers the pay period lock and the calendar read (BR-LABOR-13).</summary>
public interface IPayrollRepository
{
    /// <summary>The payrolls that are not Cancelled whose last day is <paramref name="date"/> or later, tracked, so a change dated before them can touch them.</summary>
    Task<IReadOnlyList<Payroll>> ListEndingOnOrAfterAsync(DateOnly date, CancellationToken cancellationToken);
}
