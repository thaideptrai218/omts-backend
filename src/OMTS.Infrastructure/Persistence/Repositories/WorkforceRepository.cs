using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class WorkforceRepository(OmtsDbContext db) : IWorkforceRepository
{
    // A picker shows a short list; the user narrows it with the search text.
    private const int PickerLimit = 200;

    public async Task<(IReadOnlyList<SeasonalWorker> Items, int Total)> SearchWorkersAsync(
        string? search, Guid? teamId, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.SeasonalWorkers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(w => w.FullName.Contains(text)
                || (w.PhoneNumber != null && w.PhoneNumber.Contains(text))
                || (w.CitizenId != null && w.CitizenId.Contains(text)));
        }

        if (teamId is not null)
        {
            query = query.Where(w => w.ContractorId == teamId);
        }

        if (statusId is not null)
        {
            query = query.Where(w => w.StatusId == statusId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(w => w.Contractor)
            .OrderBy(w => w.FullName).ThenBy(w => w.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<SeasonalWorker?> GetWorkerAsync(Guid id, CancellationToken cancellationToken) =>
        db.SeasonalWorkers.Include(w => w.Contractor).Include(w => w.Employee).FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SeasonalWorker>> GetWorkersAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.SeasonalWorkers.Include(w => w.Contractor).Where(w => ids.Contains(w.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.Employees.Include(e => e.Account).Include(e => e.WorkingZones).Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);

    public Task<SeasonalWorker?> FindSameWorkerAsync(
        string fullName, string? phoneNumber, string? citizenId, Guid? exceptWorkerId, CancellationToken cancellationToken)
    {
        var name = fullName.ToUpperInvariant();
        return db.SeasonalWorkers.AsNoTracking().FirstOrDefaultAsync(
            w => w.Id != exceptWorkerId
                && ((phoneNumber != null && w.PhoneNumber == phoneNumber && w.FullName.ToUpper() == name)
                    || (citizenId != null && w.CitizenId == citizenId)),
            cancellationToken);
    }

    public async Task<IReadOnlyList<Employee>> FindEmployedSamePersonAsync(string fullName, string phoneNumber, CancellationToken cancellationToken)
    {
        var name = fullName.ToUpperInvariant();
        return await db.Employees.AsNoTracking()
            .Where(e => e.StatusId == StatusIds.Employee.Working && e.PhoneNumber == phoneNumber && e.FullName.ToUpper() == name)
            .Take(2)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Contractor> Items, int Total)> SearchTeamsAsync(
        string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Contractors.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(t => t.Name.Contains(text));
        }

        if (statusId is not null)
        {
            query = query.Where(t => t.StatusId == statusId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(t => t.Leader).Include(t => t.Members)
            .OrderBy(t => t.Name).ThenBy(t => t.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<Contractor?> GetTeamAsync(Guid id, CancellationToken cancellationToken) =>
        db.Contractors.Include(t => t.Leader).Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Contractor>> GetTeamsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.Contractors.Include(t => t.Leader).Where(t => ids.Contains(t.Id)).ToListAsync(cancellationToken);

    public Task<Contractor?> FindTeamByNameAsync(string name, Guid? exceptTeamId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.Contractors.AsNoTracking().FirstOrDefaultAsync(t => t.Name.ToUpper() == upper && t.Id != exceptTeamId, cancellationToken);
    }

    public async Task<IReadOnlyList<Contractor>> GetTeamsLedByAsync(Guid workerId, CancellationToken cancellationToken) =>
        await db.Contractors.Where(t => t.LeaderWorkerId == workerId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SeasonalWorker>> GetInUseMembersAsync(IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken) =>
        teamIds.Count == 0
            ? []
            : await db.SeasonalWorkers.AsNoTracking().Include(w => w.Contractor)
                .Where(w => w.ContractorId != null && teamIds.Contains(w.ContractorId.Value) && w.StatusId == StatusIds.Usage.InUse)
                .OrderBy(w => w.FullName)
                .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> GetUnsettledPayslipPeriodsAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var payslips = await db.LaborSettlements.AsNoTracking()
            .Where(s => s.ContractorId == teamId && s.StatusId != StatusIds.LaborSettlement.Paid)
            .Select(s => new { s.Payroll.Period, s.StatusId, s.TotalObligation, Paid = s.Payments.Sum(p => (decimal?)p.AmountPaid) ?? 0 })
            .ToListAsync(cancellationToken);

        // An Approved payslip with nothing left to pay (net 0) is settled although it never becomes Fully Paid.
        return payslips
            .Where(s => s.StatusId == StatusIds.LaborSettlement.Draft || s.Paid < s.TotalObligation)
            .Select(s => s.Period)
            .Distinct()
            .Order()
            .ToList();
    }

    public async Task<PickablePeople> ListPickableAsync(Guid zoneId, string? search, CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.StatusId == StatusIds.Employee.Working && e.WorkingZones.Any(w => w.ZoneId == zoneId))
            .Where(e => text == null || e.FullName.Contains(text))
            .OrderBy(e => e.FullName).Take(PickerLimit)
            .ToListAsync(cancellationToken);
        var workers = await db.SeasonalWorkers.AsNoTracking().Include(w => w.Contractor)
            .Where(w => w.StatusId == StatusIds.Usage.InUse && (text == null || w.FullName.Contains(text)))
            .OrderBy(w => w.FullName).Take(PickerLimit)
            .ToListAsync(cancellationToken);
        var teams = await db.Contractors.AsNoTracking()
            .Where(t => t.StatusId == StatusIds.Usage.InUse && (text == null || t.Name.Contains(text)))
            .OrderBy(t => t.Name).Take(PickerLimit)
            .ToListAsync(cancellationToken);
        return new PickablePeople(employees, workers, teams);
    }

    public void AddWorker(SeasonalWorker worker) => db.SeasonalWorkers.Add(worker);

    public void AddTeam(Contractor team) => db.Contractors.Add(team);
}
