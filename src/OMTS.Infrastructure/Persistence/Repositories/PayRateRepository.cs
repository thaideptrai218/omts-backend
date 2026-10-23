using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class PayRateRepository(OmtsDbContext db) : IPayRateRepository
{
    private const int PeopleLimit = 500;

    public async Task<IReadOnlyList<PayRuleVersion>> ListForPersonAsync(Guid? employeeId, Guid? workerId, CancellationToken cancellationToken) =>
        await db.PayRuleVersions
            .Include(v => v.Employee).Include(v => v.Worker)
            .Where(v => (employeeId != null && v.EmployeeId == employeeId) || (workerId != null && v.WorkerId == workerId))
            .OrderByDescending(v => v.ValidFrom)
            .ToListAsync(cancellationToken);

    public Task<PayRuleVersion?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.PayRuleVersions.Include(v => v.Employee).Include(v => v.Worker).FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PayRuleVersion>> ListOverlappingAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.PayRuleVersions.AsNoTracking()
            .Where(v => v.StatusId == StatusIds.PayRule.Approved && v.ValidFrom <= to && (v.ValidTo == null || v.ValidTo >= from))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetFinalizedPeriodsUsingAsync(
        IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken)
    {
        var rows = await db.PayLines.AsNoTracking()
            .Where(l => l.PayRuleVersionId != null && versionIds.Contains(l.PayRuleVersionId.Value)
                && l.Settlement != null && l.Settlement.StatusId != StatusIds.LaborSettlement.Draft)
            .Select(l => new { VersionId = l.PayRuleVersionId!.Value, l.Settlement!.Payroll.Period })
            .Distinct()
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.VersionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Period).Order().ToList());
    }

    public async Task<(IReadOnlyList<Employee> Employees, IReadOnlyList<SeasonalWorker> Workers)> ListPayablePeopleAsync(
        string? search, CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.StatusId == StatusIds.Employee.Working && (text == null || e.FullName.Contains(text)))
            .OrderBy(e => e.FullName).Take(PeopleLimit)
            .ToListAsync(cancellationToken);
        var workers = await db.SeasonalWorkers.AsNoTracking().Include(w => w.Contractor)
            .Where(w => w.StatusId == StatusIds.Usage.InUse && (text == null || w.FullName.Contains(text)))
            .OrderBy(w => w.FullName).Take(PeopleLimit)
            .ToListAsync(cancellationToken);
        return (employees, workers);
    }

    public void Add(PayRuleVersion version) => db.PayRuleVersions.Add(version);
}
