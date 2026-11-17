using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// "Không thu": a Zone Manager (or the Owner) leaves lanes of a plan line out, with a reason, and the Owners are told (MSG-PLAN-06). The first lane left out starts the harvest of the plan; when every line is done the plan is Completed.
/// A mistake is put right with <see cref="UnskipAsync"/> while the line has no lot (a lane left out is put back). A line that has its lot is never changed here.
/// </summary>
public sealed class HarvestPlanLaneSkipService(
    IHarvestPlanRepository plans,
    HarvestPlanAccess access,
    HarvestPlanViews views,
    HarvestPlanProgress progress,
    HarvestPlanNotifier notifier,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<Result<HarvestPlanView>> SkipAsync(Guid planId, Guid lineId, SkipHarvestPlanLanesRequest request, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(planId, lineId, HarvestPlanRules.AllowsHarvest, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        var (plan, line) = read.Value;
        var reason = request.Reason?.Trim();
        var invalid = new List<string>();
        if (string.IsNullOrEmpty(reason) || reason.Length > HarvestPlanRules.MaxSkipReasonLength)
        {
            invalid.Add("Reason");
        }

        var lanes = Lanes(line, request.LaneNumbers, wantSkipped: false);
        if (lanes is null)
        {
            invalid.Add("Lanes");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var allDoneBefore = HarvestPlanProgress.AllDone(plan);
        var now = timeProvider.GetUtcNow();
        var me = currentUser.AccountId!.Value;
        foreach (var lane in lanes!)
        {
            lane.SkippedAt = now;
            lane.SkippedByAccountId = me;
            lane.SkipReason = reason;
        }

        var numbers = lanes.Select(l => l.Lane.LaneNumber).Order().ToList();
        progress.Settle(plan, allDoneBefore);
        audit.Record(new AuditEntry("HarvestPlan.LanesSkipped", HarvestPlanService.RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            ZoneId = line.Raft.ZoneId,
            After = new { Raft = line.Raft.Code, Lanes = numbers },
            Reason = reason,
        });
        await notifier.LanesSkippedAsync(plan, line, numbers, reason!, me, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(planId, cancellationToken);
    }

    /// <summary>Puts lanes left out back to be harvested, while the line has no lot. Not told to anyone.</summary>
    public async Task<Result<HarvestPlanView>> UnskipAsync(Guid planId, Guid lineId, SkipHarvestPlanLanesRequest request, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(planId, lineId, HarvestPlanRules.AllowsUnskip, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        var (plan, line) = read.Value;
        var lanes = Lanes(line, request.LaneNumbers, wantSkipped: true);
        if (lanes is null)
        {
            return AppError.Invalid("Lanes");
        }

        var allDoneBefore = HarvestPlanProgress.AllDone(plan);
        foreach (var lane in lanes)
        {
            lane.SkippedAt = null;
            lane.SkippedByAccountId = null;
            lane.SkipReason = null;
        }

        progress.Settle(plan, allDoneBefore);
        audit.Record(new AuditEntry("HarvestPlan.LanesUnskipped", HarvestPlanService.RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            ZoneId = line.Raft.ZoneId,
            After = new { Raft = line.Raft.Code, Lanes = lanes.Select(l => l.Lane.LaneNumber).Order().ToList() },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(planId, cancellationToken);
    }

    /// <summary>The plan and the line, after the checks both commands share: the caller sees the line and may harvest in the zone of its raft, the plan has a status the command allows, and the line has no lot (MSG-PLAN-03).</summary>
    private async Task<Result<(HarvestPlan Plan, HarvestPlanLine Line)>> ReadAsync(Guid planId, Guid lineId, Func<int, bool> allowsStatus, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null)
        {
            return AppError.NotFound;
        }

        // A Zone Manager sees only the lines of its zones: another line is not there for it.
        var line = (await access.VisibleLinesAsync(plan, cancellationToken)).FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return AppError.NotFound;
        }

        if (!await access.MayHarvestAsync(line.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (!allowsStatus(plan.StatusId))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{HarvestPlanCodeService.ObjectName} {plan.Code}", statusCatalog.DisplayNameOf(plan.StatusId)));
        }

        if (line.Lot is not null)
        {
            return AppError.Conflict(UserMessages.PlanLineAlreadyHarvested(plan.Code, line.Raft.Code), new Dictionary<string, object?> { ["lineId"] = line.Id });
        }

        return (plan, line);
    }

    /// <summary>The lanes of the line with these numbers, each given once; null when a number is not a lane of the line or is already in the wanted state (left out, or not).</summary>
    private static List<HarvestPlanLane>? Lanes(HarvestPlanLine line, IReadOnlyList<int>? numbers, bool wantSkipped)
    {
        if (numbers is null || numbers.Count == 0 || numbers.Distinct().Count() != numbers.Count)
        {
            return null;
        }

        var lanes = line.Lanes.Where(l => numbers.Contains(l.Lane.LaneNumber) && l.IsSkipped == wantSkipped).ToList();
        return lanes.Count == numbers.Count ? lanes : null;
    }

    private async Task<Result<HarvestPlanView>> ViewAsync(Guid planId, CancellationToken cancellationToken) =>
        await plans.GetForEditAsync(planId, cancellationToken) is { } plan ? await views.ViewAsync(plan, cancellationToken) : AppError.NotFound;
}
