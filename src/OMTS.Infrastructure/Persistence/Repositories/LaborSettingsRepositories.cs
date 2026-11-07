using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class JobTypeRepository(OmtsDbContext db) : IJobTypeRepository
{
    public Task<JobType?> GetForEditAsync(Guid id, CancellationToken cancellationToken) =>
        db.JobTypes.Include(j => j.Rates).FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

    // The plain comparison answers for what the column's collation calls equal, which is what the unique index refuses.
    public Task<JobType?> FindByNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.JobTypes.AsNoTracking().FirstOrDefaultAsync(j => (j.Name == name || j.Name.ToUpper() == upper) && j.Id != exceptId, cancellationToken);
    }

    public async Task<(IReadOnlyList<JobType> Items, int Total)> SearchAsync(string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
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
        var items = await query.OrderBy(j => j.Name).ThenBy(j => j.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(j => j.Rates)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<JobType>> ListInUseAsync(CancellationToken cancellationToken) =>
        await db.JobTypes.AsNoTracking().Where(j => j.StatusId == StatusIds.Usage.InUse).OrderBy(j => j.Name).ToListAsync(cancellationToken);

    public void Add(JobType jobType) => db.JobTypes.Add(jobType);

    public void AddRate(PayRate rate) => db.PayRates.Add(rate);
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
        await db.Payrolls.Where(p => p.PeriodEnd >= date).OrderBy(p => p.PeriodEnd).ToListAsync(cancellationToken);

    public async Task<DateOnly?> LastPeriodEndAsync(CancellationToken cancellationToken) =>
        await db.Payrolls.MaxAsync(p => (DateOnly?)p.PeriodEnd, cancellationToken);
}
