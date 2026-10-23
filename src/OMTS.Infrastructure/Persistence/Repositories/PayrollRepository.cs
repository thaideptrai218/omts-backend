using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class PayrollRepository(OmtsDbContext db) : IPayrollRepository
{
    public Task<Payroll?> GetAsync(string period, CancellationToken cancellationToken) =>
        WithPayslips(db.Payrolls).AsSplitQuery().FirstOrDefaultAsync(p => p.Period == period, cancellationToken);

    public async Task<IReadOnlyList<Payroll>> ListAsync(int? year, CancellationToken cancellationToken)
    {
        var query = db.Payrolls.AsNoTracking();
        if (year is not null)
        {
            var prefix = $"{year:D4}-";
            query = query.Where(p => p.Period.StartsWith(prefix));
        }

        return await WithPayslips(query).AsSplitQuery().OrderByDescending(p => p.Period).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Payroll>> ListByPeriodsAsync(IReadOnlyCollection<string> periods, CancellationToken cancellationToken) =>
        periods.Count == 0 ? [] : await db.Payrolls.Where(p => periods.Contains(p.Period)).ToListAsync(cancellationToken);

    public async Task<IReadOnlySet<string>> GetFinalizedPeriodsAsync(IReadOnlyCollection<string> periods, CancellationToken cancellationToken) =>
        periods.Count == 0
            ? new HashSet<string>()
            : (await db.Payrolls.AsNoTracking()
                .Where(p => p.FinalizedAt != null && periods.Contains(p.Period))
                .Select(p => p.Period)
                .ToListAsync(cancellationToken)).ToHashSet();

    public Task<string?> GetFirstFinalizedPeriodOverlappingAsync(DateOnly from, DateOnly? to, CancellationToken cancellationToken) =>
        db.Payrolls.AsNoTracking()
            .Where(p => p.FinalizedAt != null && p.PeriodEnd >= from && (to == null || p.PeriodStart <= to))
            .OrderBy(p => p.PeriodStart)
            .Select(p => p.Period)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<LaborSettlement?> GetSettlementAsync(Guid id, CancellationToken cancellationToken) =>
        WithPayee(db.LaborSettlements).AsSplitQuery().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<LaborSettlement>> GetSettlementsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await WithPayee(db.LaborSettlements).AsSplitQuery().Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LaborSettlement>> SearchSettlementsAsync(PayslipFilter filter, CancellationToken cancellationToken)
    {
        var query = db.LaborSettlements.AsNoTracking().Where(s => filter.StatusIds.Contains(s.StatusId));
        if (filter.Period is not null)
        {
            query = query.Where(s => s.Payroll.Period == filter.Period);
        }

        return await WithPayee(query).AsSplitQuery().OrderBy(s => s.PeriodStart).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LaborSettlement>> ListFinalizedForEmployeeAsync(
        Guid employeeId, IReadOnlyCollection<Guid> workerIds, int year, CancellationToken cancellationToken) =>
        await WithPayee(db.LaborSettlements.AsNoTracking())
            .AsSplitQuery()
            .Where(s => s.StatusId != StatusIds.LaborSettlement.Draft && s.PeriodStart.Year == year
                && (s.EmployeeId == employeeId || (s.WorkerId != null && workerIds.Contains(s.WorkerId.Value))))
            .OrderBy(s => s.PeriodStart)
            .ToListAsync(cancellationToken);

    public void Add(Payroll payroll) => db.Payrolls.Add(payroll);

    public void AddSettlement(LaborSettlement settlement) => db.LaborSettlements.Add(settlement);

    public void RemoveSettlement(LaborSettlement settlement) => db.LaborSettlements.Remove(settlement);

    public void AddLine(PayLine line) => db.PayLines.Add(line);

    public void RemoveLine(PayLine line) => db.PayLines.Remove(line);

    private static IQueryable<Payroll> WithPayslips(IQueryable<Payroll> query) =>
        query.Include(p => p.Settlements).ThenInclude(s => s.PayLines)
            .Include(p => p.Settlements).ThenInclude(s => s.Payments)
            .Include(p => p.Settlements).ThenInclude(s => s.Employee)
            .Include(p => p.Settlements).ThenInclude(s => s.Worker)
            .Include(p => p.Settlements).ThenInclude(s => s.Contractor).ThenInclude(t => t!.Leader);

    private static IQueryable<LaborSettlement> WithPayee(IQueryable<LaborSettlement> query) =>
        query.Include(s => s.Payroll)
            .Include(s => s.PayLines)
            .Include(s => s.Payments)
            .Include(s => s.Employee)
            .Include(s => s.Worker)
            .Include(s => s.Contractor).ThenInclude(t => t!.Leader);
}
