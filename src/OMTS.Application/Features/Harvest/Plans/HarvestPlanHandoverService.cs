using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// The two buttons of a Zone Manager on a plan the Owner assigned: "Nhận" (Assigned to Received, no refusal) and "Bàn giao" (Harvesting to HandedOver, once every line of the plan has its lot or has every lane left out).
/// The Owner completes a handed over plan with <see cref="HarvestPlanService.FinishAsync"/>; the system never completes one. The caller needs HARVESTS Create in a zone of a line it sees (the Owner everywhere);
/// the status is the plan's, so one Zone Manager of any zone of the plan moves it for all.
/// </summary>
public sealed class HarvestPlanHandoverService(
    IHarvestPlanRepository plans,
    HarvestPlanAccess access,
    HarvestPlanViews views,
    HarvestPlanProgress progress,
    HarvestPlanNotifier notifier,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser)
{
    /// <summary>"Nhận": the plan was assigned (Assigned); it becomes Received and lots can be recorded from it.</summary>
    public async Task<Result<HarvestPlanSaved>> ReceiveAsync(Guid planId, HarvestPlanActionRequest request, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(planId, request, HarvestPlanRules.AllowsReceive, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        progress.Move(opened.Value!, StatusIds.HarvestPlan.Received);
        return await SaveAsync(opened.Value!, cancellationToken);
    }

    /// <summary>"Bàn giao": every line of the plan has its lot or all its lanes left out; the plan becomes HandedOver and the Owners are told.</summary>
    public async Task<Result<HarvestPlanSaved>> HandOverAsync(Guid planId, HarvestPlanActionRequest request, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(planId, request, HarvestPlanRules.AllowsHandOver, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var plan = opened.Value!;
        if (!HarvestPlanProgress.AllDone(plan))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{HarvestPlanCodeService.ObjectName} {plan.Code}", "with lines still to harvest"));
        }

        progress.Move(plan, StatusIds.HarvestPlan.HandedOver);
        await notifier.HandedOverAsync(plan, currentUser.AccountId!.Value, cancellationToken);
        return await SaveAsync(plan, cancellationToken);
    }

    /// <summary>The plan, after the checks both buttons share: the caller sees it and may harvest in a zone of a line it sees, the status allows the button, and the row version is current.</summary>
    private async Task<Result<HarvestPlan>> OpenAsync(Guid planId, HarvestPlanActionRequest request, Func<int, bool> allowsStatus, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !await access.MayReadAsync(plan, cancellationToken))
        {
            return AppError.NotFound;
        }

        var mayHarvest = false;
        foreach (var zoneId in (await access.VisibleLinesAsync(plan, cancellationToken)).Select(l => l.Raft.ZoneId).Distinct())
        {
            mayHarvest |= await access.MayHarvestAsync(zoneId, cancellationToken);
        }

        if (!mayHarvest)
        {
            return AppError.Forbidden;
        }

        if (!allowsStatus(plan.StatusId))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{HarvestPlanCodeService.ObjectName} {plan.Code}", statusCatalog.DisplayNameOf(plan.StatusId)));
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return plan;
    }

    private async Task<Result<HarvestPlanSaved>> SaveAsync(HarvestPlan plan, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (await plans.GetForEditAsync(plan.Id, cancellationToken) is not { } saved)
        {
            return AppError.NotFound;
        }

        var message = UserMessages.Saved($"{HarvestPlanCodeService.ObjectName} {saved.Code}");
        return new HarvestPlanSaved(await views.ViewAsync(saved, cancellationToken), message.Text, message.Code);
    }
}
