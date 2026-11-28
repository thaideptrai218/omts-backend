using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories.Workforce;

internal sealed class WorkDayPlanRepository(OmtsDbContext db) : IWorkDayPlanRepository
{
    // The older plan of one raft and one job type, with the purchase request it may be tied to.
    private IQueryable<WorkDayPlan> Plans => db.Set<WorkDayPlan>()
        .Include(p => p.Zone).Include(p => p.Raft).Include(p => p.JobType)
        .Include(p => p.CreatorAccount).Include(p => p.ResponsibleAccount)
        .Include(p => p.PurchaseRequest!).ThenInclude(r => r.ShiftInvitations).ThenInclude(i => i.Employee).ThenInclude(e => e.Account);

    // The work-day plan with everything the rules and the views read.
    private IQueryable<WorkDayPlan> Full => db.Set<WorkDayPlan>()
        .Include(p => p.Zone).Include(p => p.Raft).Include(p => p.NurseryBatch)
        .Include(p => p.CreatorAccount).Include(p => p.ResponsibleAccount)
        .Include(p => p.HarvestPlanLine!).ThenInclude(l => l.Plan)
        .Include(p => p.Members).ThenInclude(m => m.Employee).ThenInclude(e => e.Account)
        .Include(p => p.OutsiderHires)
        .AsSplitQuery();

    public Task<WorkDayPlan?> GetAsync(Guid id, CancellationToken ct) => Plans.FirstOrDefaultAsync(p => p.Id == id && p.JobTypeId != null, ct);
    public Task<WorkDayPlan?> ForPurchaseAsync(Guid purchaseRequestId, CancellationToken ct) => Plans.FirstOrDefaultAsync(p => p.PurchaseRequestId == purchaseRequestId, ct);
    public Task<WorkDayPlan?> FindOperationAsync(Guid creatorId, Guid operationId, CancellationToken ct) =>
        Plans.FirstOrDefaultAsync(p => p.CreatorAccountId == creatorId && p.ClientOperationId == operationId, ct);
    public async Task<IReadOnlyList<WorkDayPlan>> SearchAsync(DateOnly from, DateOnly to, Guid? responsibleId, CancellationToken ct) =>
        await Plans.Where(p => p.JobTypeId != null && p.WorkDate >= from && p.WorkDate <= to && (responsibleId == null || p.ResponsibleAccountId == responsibleId))
            .OrderBy(p => p.WorkDate).ThenByDescending(p => p.CreatedAt).ToListAsync(ct);
    public async Task<IReadOnlyList<Account>> ResponsibleCandidatesAsync(CancellationToken ct) =>
        await db.Accounts.Where(a => a.SubjectType == SubjectType.Staff && a.StatusId == StatusIds.Account.Active)
            .OrderBy(a => a.DisplayName).ToListAsync(ct);
    public void Add(WorkDayPlan plan) => db.Set<WorkDayPlan>().Add(plan);

    public Task<WorkDayPlan?> GetForEditAsync(Guid id, CancellationToken ct) => Full.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<WorkDayPlan?> GetByLineForEditAsync(Guid lineId, CancellationToken ct) => Full.FirstOrDefaultAsync(p => p.HarvestPlanLineId == lineId, ct);

    public async Task<IReadOnlyList<WorkDayPlan>> ListByLinesForEditAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken ct)
    {
        var ids = lineIds.Distinct().ToList();
        return ids.Count == 0 ? [] : await Full.Where(p => p.HarvestPlanLineId != null && ids.Contains(p.HarvestPlanLineId.Value)).ToListAsync(ct);
    }

    public Task<WorkDayPlan?> FindOperationForEditAsync(Guid creatorId, Guid operationId, CancellationToken ct) =>
        Full.FirstOrDefaultAsync(p => p.CreatorAccountId == creatorId && p.ClientOperationId == operationId, ct);

    public Task<WorkDayPlan?> FindByCodeAsync(string code, Guid? exceptPlanId, CancellationToken ct) =>
        db.Set<WorkDayPlan>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == code && p.Id != exceptPlanId, ct);

    public async Task<IReadOnlyList<WorkDayPlan>> ListAsync(WorkDayPlanFilter filter, CancellationToken ct)
    {
        var query = Full.AsNoTracking().Where(p => p.WorkDate >= filter.From && p.WorkDate <= filter.To);
        if (filter.ZoneIds is { Count: > 0 } zones)
        {
            var ids = zones.ToList();
            query = query.Where(p => ids.Contains(p.ZoneId));
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(p => p.Kind == kind);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        return await query.OrderBy(p => p.WorkDate).ThenBy(p => p.Code).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ListPlanIdsWithOverdueInvitationsAsync(DateTimeOffset now, CancellationToken ct) =>
        await db.Set<WorkDayPlanMember>().AsNoTracking()
            .Where(m => m.State == WorkDayMemberState.Invited && m.Deadline != null && m.Deadline <= now)
            .Select(m => m.PlanId).Distinct().ToListAsync(ct);

    public async Task<IReadOnlyList<WorkDayPlanMember>> ListMembersOnDayAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, Guid exceptPlanId, CancellationToken ct)
    {
        var ids = employeeIds.ToList();
        return await db.Set<WorkDayPlanMember>()
            .Include(m => m.Plan).ThenInclude(p => p.Members)
            .Include(m => m.Plan).ThenInclude(p => p.OutsiderHires)
            .Include(m => m.Plan).ThenInclude(p => p.ResponsibleAccount)
            .Include(m => m.Employee)
            .Where(m => ids.Contains(m.EmployeeId) && m.Plan.WorkDate == day && m.PlanId != exceptPlanId
                && (m.State == WorkDayMemberState.Accepted || m.State == WorkDayMemberState.ConfirmedOnBehalf))
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PlanOutsiderHire>> ListOutsiderHiresAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.Set<PlanOutsiderHire>().AsNoTracking().Include(h => h.Plan)
            .Where(h => h.Plan.WorkDate >= from && h.Plan.WorkDate <= to && h.Plan.Status != WorkDayPlanStatus.Cancelled)
            .ToListAsync(ct);

    public Task<PlanOutsiderHire?> GetOutsiderForEditAsync(Guid id, CancellationToken ct) =>
        db.Set<PlanOutsiderHire>().Include(h => h.Plan).FirstOrDefaultAsync(h => h.Id == id, ct);

    public async Task<IReadOnlyDictionary<Guid, string>> VoucherCodesAsync(IReadOnlyCollection<Guid> voucherIds, CancellationToken ct)
    {
        var ids = voucherIds.Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.CashVouchers.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Code, ct);
    }

    public void AddMember(WorkDayPlanMember member) => db.Set<WorkDayPlanMember>().Add(member);
    public void RemoveMember(WorkDayPlanMember member) => db.Set<WorkDayPlanMember>().Remove(member);
    public void AddOutsider(PlanOutsiderHire hire) => db.Set<PlanOutsiderHire>().Add(hire);
    public void RemoveOutsider(PlanOutsiderHire hire) => db.Set<PlanOutsiderHire>().Remove(hire);
}
