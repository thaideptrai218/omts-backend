using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class HarvestPlanStaffRepository(OmtsDbContext db) : IHarvestPlanStaffRepository
{
    public Task<HarvestPlanStaffMember?> GetForEditAsync(Guid rowId, CancellationToken cancellationToken) =>
        WithPlan(db.HarvestPlanStaffMembers).FirstOrDefaultAsync(r => r.Id == rowId, cancellationToken);

    public async Task<IReadOnlyList<HarvestPlanStaffMember>> ListOfEmployeeAsync(Guid employeeId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var rows = db.HarvestPlanStaffMembers.AsNoTrackingWithIdentityResolution().Where(r => r.EmployeeId == employeeId);
        rows = from is null && to is null
            ? rows.Where(r => r.Plan.StatusId != StatusIds.HarvestPlan.Completed && r.Plan.StatusId != StatusIds.HarvestPlan.Cancelled)
            : rows.Where(r => (from == null || r.Plan.PlannedDate >= from) && (to == null || r.Plan.PlannedDate <= to));
        return await WithPlan(rows).OrderByDescending(r => r.Plan.PlannedAt).ThenBy(r => r.Plan.Code).ToListAsync(cancellationToken);
    }

    public void Add(HarvestPlanStaffMember row) => db.HarvestPlanStaffMembers.Add(row);

    public void Remove(HarvestPlanStaffMember row) => db.HarvestPlanStaffMembers.Remove(row);

    private static IQueryable<HarvestPlanStaffMember> WithPlan(IQueryable<HarvestPlanStaffMember> rows) =>
        rows.Include(r => r.Employee).ThenInclude(e => e.TypePeriods)
            .Include(r => r.Plan).ThenInclude(p => p.Lines).ThenInclude(l => l.Raft).ThenInclude(raft => raft.Zone)
            .Include(r => r.Plan).ThenInclude(p => p.Staff).ThenInclude(s => s.Employee).ThenInclude(e => e.TypePeriods)
            .AsSplitQuery();
}
