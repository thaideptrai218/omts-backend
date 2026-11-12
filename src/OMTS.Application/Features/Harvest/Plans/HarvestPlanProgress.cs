using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>The line of a plan a lot is recorded from, as the recording sees it: the plan and line, tracked, and the lanes to harvest (the plan's lanes without those left out).</summary>
public sealed record PlannedHarvest(HarvestPlan Plan, HarvestPlanLine Line, IReadOnlyList<int> LaneNumbers);

/// <summary>
/// How lots and lanes left out move a plan along, in the unit of work of the change that causes them: a lot recorded from a line, a lot removed, lanes left out or put back, a line changed. The moves follow
/// <see cref="HarvestPlanRules.Settle"/>; each one is audited, stamps the plan and gives it a new version, so two Zone Managers recording at once do not both settle the plan from stale lines.
/// </summary>
public sealed class HarvestPlanProgress(
    IHarvestPlanRepository plans, IStatusCatalog statusCatalog, AuditLogService audit, IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider)
{
    public const string FieldName = "Harvest plan line";

    /// <summary>
    /// The line a lot is recorded from, for the recording of a harvest on <paramref name="raftId"/>: the line must be of that raft, without a lot (MSG-PLAN-03), and its plan Assigned or Harvesting (MSG-GEN-09). The lanes
    /// are the lanes of the plan not left out; a line with every lane left out records nothing (MSG-GEN-09).
    /// </summary>
    public async Task<Result<PlannedHarvest>> ResolveLineAsync(Guid lineId, Guid raftId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetByLineForEditAsync(lineId, cancellationToken);
        var line = plan?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (plan is null || line is null || line.RaftId != raftId)
        {
            return AppError.Invalid(FieldName);
        }

        if (!HarvestPlanRules.AllowsHarvest(plan.StatusId))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{HarvestPlanCodeService.ObjectName} {plan.Code}", statusCatalog.DisplayNameOf(plan.StatusId)));
        }

        if (line.Lot is not null)
        {
            return AppError.Conflict(UserMessages.PlanLineAlreadyHarvested(plan.Code, line.Raft.Code));
        }

        var lanes = line.Lanes.Where(l => !l.IsSkipped).Select(l => l.Lane.LaneNumber).Order().ToList();
        if (lanes.Count == 0)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Raft {line.Raft.Code} of {HarvestPlanCodeService.ObjectName} {plan.Code}", "left out"));
        }

        return new PlannedHarvest(plan, line, lanes);
    }

    /// <summary>The lot is recorded from the line: it carries the link, and the plan moves on (Harvesting, then Completed when every line is done).</summary>
    public void OnLotRecorded(PlannedHarvest planned, HarvestLot lot)
    {
        lot.HarvestPlanLineId = planned.Line.Id;
        lot.HarvestPlanLine = planned.Line;
        planned.Line.Lot = lot;
        Settle(planned.Plan, allDoneBefore: false);
    }

    /// <summary>
    /// The lot is about to be removed: its line is open again, and a plan that had finished by itself is Harvesting again. A plan the Owner finished with lanes still to do stays Completed. Call it before the lot is removed.
    /// </summary>
    public async Task OnLotRemovedAsync(HarvestLot lot, CancellationToken cancellationToken)
    {
        if (lot.HarvestPlanLineId is not { } lineId || await plans.GetByLineForEditAsync(lineId, cancellationToken) is not { } plan)
        {
            return;
        }

        var line = plan.Lines.First(l => l.Id == lineId);
        var allDoneBefore = AllDone(plan);
        line.Lot = null;
        Settle(plan, allDoneBefore);
    }

    public static bool AllDone(HarvestPlan plan) => HarvestPlanRules.AllDone(plan.Lines.Select(HarvestPlanRules.FactsOf).ToList());

    /// <summary>
    /// Applies the moves of the status after a lot or a lane left out or put back, or a change of the lines. The plan is marked changed whenever this runs, so its version moves with the lines. Returns whether the status moved.
    /// </summary>
    public bool Settle(HarvestPlan plan, bool allDoneBefore)
    {
        unitOfWork.MarkChanged(plan);
        var moves = HarvestPlanRules.Settle(plan.StatusId, plan.Lines.Select(HarvestPlanRules.FactsOf).ToList(), allDoneBefore);
        foreach (var target in moves)
        {
            Move(plan, target);
        }

        return moves.Count > 0;
    }

    /// <summary>Moves a plan to another status, with the stamps of that status, and audits it.</summary>
    public void Move(HarvestPlan plan, int target)
    {
        var from = plan.StatusId;
        plan.StatusId = target;
        if (target == StatusIds.HarvestPlan.Completed)
        {
            plan.CompletedAt = timeProvider.GetUtcNow();
            plan.CompletedByAccountId = currentUser.AccountId;
        }
        else if (target == StatusIds.HarvestPlan.Harvesting)
        {
            plan.CompletedAt = null;
            plan.CompletedByAccountId = null;
        }

        unitOfWork.MarkChanged(plan);
        audit.RecordStatusChange(nameof(HarvestPlan), plan.Id, plan.Code, from, target);
    }
}
