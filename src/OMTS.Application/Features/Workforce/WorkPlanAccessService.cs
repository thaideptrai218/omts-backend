using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Workforce;

/// <summary>Quyền vùng chưa đủ: chỉ người được giao kế hoạch đã nhận việc mới được phân công.</summary>
public sealed class WorkPlanAccessService(ICurrentUser user, PermissionService permissions)
{
    public Task<AccountAccess?> CurrentAsync(CancellationToken ct) => user.AccountId is { } id
        ? permissions.GetAccessAsync(id, ct) : Task.FromResult<AccountAccess?>(null);
    public async Task<bool> CanViewAsync(WorkDayPlan plan, CancellationToken ct)
    {
        var who = await CurrentAsync(ct);
        return who?.IsCooperativeOwner == true || who?.AccountId == plan.ResponsibleAccountId &&
            who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.ZoneScoped, plan.Raft.ZoneId);
    }
    public async Task<bool> CanAssignAsync(WorkDayPlan plan, CancellationToken ct)
    {
        var who = await CurrentAsync(ct);
        return plan.Approved == true && who?.AccountId == plan.ResponsibleAccountId &&
            plan.ResponsibleAccount.StatusId == StatusIds.Account.Active &&
            who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.ZoneScoped, plan.Raft.ZoneId);
    }
}
