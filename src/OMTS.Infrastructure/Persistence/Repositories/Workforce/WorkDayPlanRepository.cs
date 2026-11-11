using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories.Workforce;

internal sealed class WorkDayPlanRepository(OmtsDbContext db) : IWorkDayPlanRepository
{
    private IQueryable<WorkDayPlan> Plans => db.Set<WorkDayPlan>()
        .Include(p => p.Raft).ThenInclude(r => r.Zone).Include(p => p.JobType)
        .Include(p => p.CreatorAccount).Include(p => p.ResponsibleAccount)
        .Include(p => p.PurchaseRequest!).ThenInclude(r => r.ShiftInvitations).ThenInclude(i => i.Employee).ThenInclude(e => e.Account);
    public Task<WorkDayPlan?> GetAsync(Guid id, CancellationToken ct) => Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
    public Task<WorkDayPlan?> ForPurchaseAsync(Guid purchaseRequestId, CancellationToken ct) => Plans.FirstOrDefaultAsync(p => p.PurchaseRequestId == purchaseRequestId, ct);
    public Task<WorkDayPlan?> FindOperationAsync(Guid creatorId, Guid operationId, CancellationToken ct) =>
        Plans.FirstOrDefaultAsync(p => p.CreatorAccountId == creatorId && p.ClientOperationId == operationId, ct);
    public async Task<IReadOnlyList<WorkDayPlan>> SearchAsync(DateOnly from, DateOnly to, Guid? responsibleId, CancellationToken ct) =>
        await Plans.Where(p => p.WorkDate >= from && p.WorkDate <= to && (responsibleId == null || p.ResponsibleAccountId == responsibleId))
            .OrderBy(p => p.WorkDate).ThenByDescending(p => p.CreatedAt).ToListAsync(ct);
    public async Task<IReadOnlyList<Account>> ResponsibleCandidatesAsync(CancellationToken ct) =>
        await db.Accounts.Where(a => a.SubjectType == OMTS.Domain.Enums.SubjectType.Staff && a.StatusId == StatusIds.Account.Active)
            .OrderBy(a => a.DisplayName).ToListAsync(ct);
    public void Add(WorkDayPlan plan) => db.Set<WorkDayPlan>().Add(plan);
}
