using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class JobTypeRepository(OmtsDbContext db) : IJobTypeRepository
{
    public Task<JobType?> GetForEditAsync(Guid id, CancellationToken cancellationToken) =>
        db.JobTypes.Include(j => j.Rates).Include(j => j.ShiftRates).AsSplitQuery().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

    // The plain comparison answers for what the column's collation calls equal, which is what the unique index refuses.
    public Task<JobType?> FindByNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        return db.JobTypes.AsNoTracking().FirstOrDefaultAsync(j => j.Name == name && j.Id != exceptId, cancellationToken);
    }

    public async Task<(IReadOnlyList<JobType> Items, int Total)> SearchAsync(
        string? search, int? statusId, JobTypeSort sort, bool descending, DateOnly today, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.JobTypes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(j => j.Name.Contains(text));
        }

        if (statusId is not null)
        {
            query = query.Where(j => j.StatusId == statusId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Ordered(query, sort, descending, today).ThenBy(j => j.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(j => j.Rates)
            .Include(j => j.ShiftRates)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    /// <summary>The rate and its date are those in force on <paramref name="today"/> (<c>PayRate.EffectiveOn</c>), written inline so they become SQL.</summary>
    private static IOrderedQueryable<JobType> Ordered(IQueryable<JobType> query, JobTypeSort sort, bool descending, DateOnly today) => sort switch
    {
        JobTypeSort.Rate => descending
            ? query.OrderByDescending(j => j.Rates.Where(r => r.EffectiveFrom <= today).OrderByDescending(r => r.EffectiveFrom).Select(r => (decimal?)r.HourlyRate).FirstOrDefault())
            : query.OrderBy(j => j.Rates.Where(r => r.EffectiveFrom <= today).OrderByDescending(r => r.EffectiveFrom).Select(r => (decimal?)r.HourlyRate).FirstOrDefault()),
        JobTypeSort.EffectiveFrom => descending
            ? query.OrderByDescending(j => j.Rates.Where(r => r.EffectiveFrom <= today).Max(r => (DateOnly?)r.EffectiveFrom))
            : query.OrderBy(j => j.Rates.Where(r => r.EffectiveFrom <= today).Max(r => (DateOnly?)r.EffectiveFrom)),
        JobTypeSort.Status => descending ? query.OrderByDescending(j => j.StatusId) : query.OrderBy(j => j.StatusId),
        _ => descending ? query.OrderByDescending(j => j.Name) : query.OrderBy(j => j.Name),
    };

    public async Task<IReadOnlyList<JobType>> ListInUseAsync(CancellationToken cancellationToken) =>
        await db.JobTypes.AsNoTracking().Where(j => j.StatusId == StatusIds.Usage.InUse).OrderBy(j => j.Name).ToListAsync(cancellationToken);

    public void Add(JobType jobType) => db.JobTypes.Add(jobType);

    public void AddRate(PayRate rate) => db.PayRates.Add(rate);

    public void AddShiftRate(ShiftRate rate) => db.ShiftRates.Add(rate);
}

internal sealed class EmployeeSalaryRepository(OmtsDbContext db) : IEmployeeSalaryRepository
{
    public Task<Employee?> GetWithSalarySetupsAsync(Guid employeeId, CancellationToken cancellationToken) =>
        db.Employees
            .Include(e => e.TypePeriods)
            .Include(e => e.SalarySetups)
            .AsSplitQuery()
            .FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, SalaryType>> TypesOnAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return new Dictionary<Guid, SalaryType>();
        }

        // The latest setup on or before the day, as EmployeeSalarySetup.EffectiveOn picks it, in one query for the whole page.
        var rows = await db.EmployeeSalarySetups.AsNoTracking()
            .Where(s => employeeIds.Contains(s.EmployeeId) && s.EffectiveFrom <= day)
            .Where(s => s.EffectiveFrom == db.EmployeeSalarySetups.Where(o => o.EmployeeId == s.EmployeeId && o.EffectiveFrom <= day).Max(o => o.EffectiveFrom))
            .Select(s => new { s.EmployeeId, s.Type })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.EmployeeId, r => r.Type);
    }

    public void Add(EmployeeSalarySetup setup) => db.EmployeeSalarySetups.Add(setup);
}

internal sealed class AttendanceSettingsRepository(OmtsDbContext db) : IAttendanceSettingsRepository
{
    public async Task<IReadOnlyList<AttendanceSettingsVersion>> ListAsync(CancellationToken cancellationToken) =>
        await db.AttendanceSettingsVersions.OrderByDescending(v => v.EffectiveFrom).ToListAsync(cancellationToken);

    public void Add(AttendanceSettingsVersion version) => db.AttendanceSettingsVersions.Add(version);
}

internal sealed class PayrollRepository(OmtsDbContext db) : IPayrollRepository
{
    public async Task<IReadOnlyList<Payroll>> ListEndingOnOrAfterAsync(DateOnly date, CancellationToken cancellationToken) =>
        await db.Payrolls.Where(p => p.PeriodEnd >= date && p.StatusId != StatusIds.Payroll.Cancelled).OrderBy(p => p.PeriodEnd).ToListAsync(cancellationToken);
}
