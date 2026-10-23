using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class WorkAssignmentRepository(OmtsDbContext db) : IWorkAssignmentRepository
{
    public Task<WorkAssignment?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        WithAssignees(db.WorkAssignments).FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<WorkAssignment> Items, int Total)> SearchAsync(
        Guid zoneId, WorkAssignmentFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.WorkAssignments.AsNoTracking()
            .Where(a => a.ZoneId == zoneId && a.WorkDate >= filter.From && a.WorkDate <= filter.To);
        if (filter.EmployeeId is not null)
        {
            query = query.Where(a => a.EmployeeId == filter.EmployeeId);
        }

        if (filter.WorkerId is not null)
        {
            query = query.Where(a => a.WorkerId == filter.WorkerId);
        }

        if (filter.TeamId is not null)
        {
            query = query.Where(a => a.ContractorId == filter.TeamId);
        }

        if (filter.PaymentType is not null)
        {
            query = query.Where(a => a.PaymentType == filter.PaymentType);
        }

        query = filter.State switch
        {
            "active" => query.Where(a => a.CancelledAt == null && a.CompletionConfirmedAt == null),
            "cancelled" => query.Where(a => a.CancelledAt != null),
            "completed" => query.Where(a => a.CancelledAt == null && a.CompletionConfirmedAt != null),
            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);
        var items = await WithAssignees(query)
            .OrderByDescending(a => a.WorkDate).ThenBy(a => a.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<WorkAssignment>> ListActiveOnDateAsync(
        DateOnly date, IReadOnlyCollection<Guid> employeeIds, IReadOnlyCollection<Guid> workerIds, IReadOnlyCollection<Guid> teamIds,
        Guid? exceptAssignmentId, CancellationToken cancellationToken) =>
        await WithAssignees(db.WorkAssignments.AsNoTracking())
            .Where(a => a.WorkDate == date && a.CancelledAt == null && a.Id != exceptAssignmentId)
            .Where(a => (a.EmployeeId != null && employeeIds.Contains(a.EmployeeId.Value))
                || (a.WorkerId != null && workerIds.Contains(a.WorkerId.Value))
                || (a.ContractorId != null && teamIds.Contains(a.ContractorId.Value)))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkAssignment>> ListActiveDailyWageAsync(Guid zoneId, DateOnly date, CancellationToken cancellationToken) =>
        await WithAssignees(db.WorkAssignments.AsNoTracking())
            .Where(a => a.ZoneId == zoneId && a.WorkDate == date && a.CancelledAt == null && a.PaymentType == WorkPaymentType.DailyWage)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkAssignment>> ListConfirmedPieceworkAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.WorkAssignments.AsNoTracking()
            .Where(a => a.PaymentType == WorkPaymentType.Piecework && a.CancelledAt == null && a.CompletionConfirmedAt != null
                && a.CompletionDate >= from && a.CompletionDate <= to)
            .ToListAsync(cancellationToken);

    public Task<Raft?> GetRaftAsync(Guid id, CancellationToken cancellationToken) =>
        db.Rafts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<CultivationCycle?> GetCycleAsync(Guid id, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<FarmingZone?> GetZoneAsync(Guid id, CancellationToken cancellationToken) =>
        db.FarmingZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == id, cancellationToken);

    public void Add(WorkAssignment assignment) => db.WorkAssignments.Add(assignment);

    private static IQueryable<WorkAssignment> WithAssignees(IQueryable<WorkAssignment> query) =>
        query.Include(a => a.Zone)
            .Include(a => a.Employee).ThenInclude(e => e!.Account)
            .Include(a => a.Worker)
            .Include(a => a.Contractor);
}
