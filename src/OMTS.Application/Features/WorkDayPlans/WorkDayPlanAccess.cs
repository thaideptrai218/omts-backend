using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>Who is in charge of the plan of a harvest plan line (a Zone Manager of the raft's zone or an Owner).</summary>
public interface IHarvestResponsibleLookup
{
    Task<Guid?> ResponsibleForHarvestAsync(Guid zoneId, HarvestPlanExecutor executor, Guid? callerId, CancellationToken cancellationToken);
}

/// <summary>
/// Who may do what with the work-day plans. The Owner acts on every zone. A Zone Manager (WORK_ASSIGNMENTS in the zone) sees and staffs the plans of its zones: it invites, confirms on behalf, takes people off and adds
/// outsiders, and makes, changes and cancels the nursery and seeding plans of its zones. A harvest plan comes only from a line of a harvest plan, so only the Owner changes it, and a Zone Manager sees it only once the
/// harvest plan was handed to the Zone Managers (never when the Owner harvests it himself).
/// </summary>
public sealed class WorkDayPlanAccess(WorkPlanAccessService workPlanAccess, IWorkDayPlanRepository plans, IAccountRepository accounts, IZoneRecipientQuery recipients) : IHarvestResponsibleLookup
{
    public Task<AccountAccess?> CurrentAsync(CancellationToken cancellationToken) => workPlanAccess.CurrentAsync(cancellationToken);

    public static bool MaySee(AccountAccess who, WorkDayPlan plan) =>
        who.IsCooperativeOwner || HarvestVisible(plan) && who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.ZoneScoped, plan.ZoneId);

    /// <summary>Invite, confirm, take off and add outsiders.</summary>
    public static bool MayStaff(AccountAccess who, WorkDayPlan plan) =>
        who.IsCooperativeOwner || MaySee(who, plan) && who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.ZoneScoped, plan.ZoneId);

    /// <summary>Change, and set the number of people needed: the Owner for any plan, a Zone Manager for a nursery or seeding plan of its zones.</summary>
    public static bool MayEdit(AccountAccess who, WorkDayPlan plan) =>
        who.IsCooperativeOwner || plan.Kind != WorkDayPlanKind.Harvest && MaySee(who, plan)
            && who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.ZoneScoped, plan.ZoneId);

    public static bool MayCancel(AccountAccess who, WorkDayPlan plan) =>
        plan.Kind != WorkDayPlanKind.Harvest && (who.IsCooperativeOwner || MaySee(who, plan)
            && who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Delete, PermissionScope.ZoneScoped, plan.ZoneId));

    public static bool MayPayOutsiders(AccountAccess who) => who.Allows(PermissionFunctions.Payroll, PermissionAction.Create, PermissionScope.NoZone, null);

    /// <summary>The plan of a line of a harvest plan the Owner harvests himself, or that was not handed over yet, is not for a Zone Manager.</summary>
    private static bool HarvestVisible(WorkDayPlan plan) =>
        plan.Kind != WorkDayPlanKind.Harvest || plan.HarvestPlanLine is not { } line
        || line.Plan.AssignedAt is not null && line.Plan.Executor == HarvestPlanExecutor.ZoneManager;

    /// <summary>
    /// The people who can be in charge of a plan in the zone: the caller, and for the Owner also the Zone Managers who hold WORK_ASSIGNMENTS Create and View in the zone. A Zone Manager plans for itself only.
    /// </summary>
    public async Task<IReadOnlyList<WorkDayPlanResponsible>> ResponsiblesAsync(AccountAccess who, Guid zoneId, CancellationToken cancellationToken)
    {
        var self = await accounts.GetByIdAsync(who.AccountId, cancellationToken);
        var result = new List<WorkDayPlanResponsible> { new(who.AccountId, self?.DisplayName ?? string.Empty, true) };
        if (!who.IsCooperativeOwner)
        {
            return result;
        }

        foreach (var candidate in await ManagersOfZoneAsync(zoneId, cancellationToken))
        {
            if (candidate.Id != who.AccountId)
            {
                result.Add(new(candidate.Id, candidate.DisplayName, false));
            }
        }

        return result;
    }

    /// <summary>The active staff accounts, other than an Owner, that hold WORK_ASSIGNMENTS Create and View in the zone, by name.</summary>
    public async Task<IReadOnlyList<Domain.Entities.Identity.Account>> ManagersOfZoneAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var managers = new List<Domain.Entities.Identity.Account>();
        foreach (var candidate in await plans.ResponsibleCandidatesAsync(cancellationToken))
        {
            if (await workPlanAccess.AccessOfAsync(candidate.Id, cancellationToken) is { IsCooperativeOwner: false } rights
                && rights.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.ZoneScoped, zoneId)
                && rights.Allows(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.ZoneScoped, zoneId))
            {
                managers.Add(candidate);
            }
        }

        return managers;
    }

    /// <summary>The person in charge of a harvest plan line: a Zone Manager of the raft's zone when the Zone Managers harvest, otherwise (or when the zone has none) an Owner, the caller when it is one.</summary>
    public async Task<Guid?> ResponsibleForHarvestAsync(Guid zoneId, HarvestPlanExecutor executor, Guid? callerId, CancellationToken cancellationToken)
    {
        if (executor == HarvestPlanExecutor.ZoneManager && (await ManagersOfZoneAsync(zoneId, cancellationToken)).FirstOrDefault() is { } manager)
        {
            return manager.Id;
        }

        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        return callerId is { } id && owners.Contains(id) ? id : owners.Count > 0 ? owners[0] : null;
    }
}
