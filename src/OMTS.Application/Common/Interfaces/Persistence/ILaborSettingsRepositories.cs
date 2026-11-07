using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>LABOR-11: job types with their rates.</summary>
public interface IJobTypeRepository
{
    /// <summary>The job type with its rates, tracked.</summary>
    Task<JobType?> GetForEditAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Another job type with the name, whatever its letter case (BR-LABOR-12).</summary>
    Task<JobType?> FindByNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<JobType> Items, int Total)> SearchAsync(string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The job types that can be chosen for profiles and work (GBR-01), by name.</summary>
    Task<IReadOnlyList<JobType>> ListInUseAsync(CancellationToken cancellationToken);

    void Add(JobType jobType);

    void AddRate(PayRate rate);
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
    /// <summary>The payrolls whose last day is <paramref name="date"/> or later, tracked, so a change dated before them can touch them.</summary>
    Task<IReadOnlyList<Payroll>> ListEndingOnOrAfterAsync(DateOnly date, CancellationToken cancellationToken);

    /// <summary>The last day of the latest pay period that has a payroll; null when there is none.</summary>
    Task<DateOnly?> LastPeriodEndAsync(CancellationToken cancellationToken);
}
