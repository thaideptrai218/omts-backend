using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Closing;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>CYCLE-05 View cultivation progress, on a date or for a cycle, for a Zone Manager of the raft's zone and the Cooperative Owner. Viewing changes nothing.</summary>
public sealed class CultivationProgressService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    LaneStateReader laneStates,
    RaftViews raftViews,
    CultivationCycleViews cycleViews,
    CultivationLogViews logViews,
    SeedingReadService seeding,
    CycleSummaryReader summaries,
    ZoneAccessGuard zoneAccess,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Without a date or cycle: today. With a cycle: its close date, or today for an Open one, unless <paramref name="date"/> names
    /// another day of the cycle. A date in the future has no state yet: MSG-GEN-01.
    /// </summary>
    public async Task<Result<CultivationProgressView>> ViewAsync(Guid raftId, DateOnly? date, Guid? cycleId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var today = timeProvider.Today();
        var shown = date ?? today;
        if (cycleId is { } id)
        {
            var chosen = await cycles.GetAsync(id, cancellationToken);
            if (chosen is null || chosen.RaftId != raft.Id)
            {
                return AppError.NotFound;
            }

            shown = date ?? chosen.CloseDate ?? today;
            if (shown < chosen.StartDate || (chosen.CloseDate is { } closed && shown > closed))
            {
                return AppError.Invalid("Date", "Choose a date within the cycle.");
            }
        }

        if (shown > today)
        {
            return AppError.Invalid("Date", "Choose today or an earlier date.");
        }

        var day = await laneStates.ReadAsync(raft, shown, cancellationToken);
        var cycle = day.Cycle is { } span ? await cycles.GetAsync(span.Id, cancellationToken) : null;
        var onView = raft.Lanes
            .Where(l => (l.StatusId == StatusIds.Lane.InUse && WasOnRaft(l, shown, cycle)) || day.LanesWithRecords.Contains(l.Id))
            .Select(l => l.Id)
            .ToHashSet();
        var lanes = raftViews.LanesOn(raft, day.States).Where(l => onView.Contains(l.Id)).ToList();
        return new CultivationProgressView(
            raft.Id,
            raft.Code,
            shown,
            cycle is null ? null : await cycleViews.ViewAsync(cycle, cancellationToken),
            lanes,
            lanes.Where(l => l.StatusId == StatusIds.Lane.InUse && l.State == LaneState.NotSeeded && !l.IsOutOfService).Select(l => l.LaneNumber).ToList(),
            cycle is null ? [] : await seeding.LaneSeedLotsAsync(cycle.Id, shown, cancellationToken),
            cycle is null ? [] : await logViews.ListAsync(cycle, cancellationToken),
            cycle?.StatusId == StatusIds.CultivationCycle.Closed ? await summaries.ReadAsync(cycle, cancellationToken) : null);
    }

    /// <summary>
    /// A lane created after the date, in Vietnam time, is not on that date. A start can be dated back, and a raft can be recorded after
    /// its first cycle began, so a lane already there when the cycle was recorded belongs to every day of it. No lane is added while
    /// a cycle is Open, so an Open cycle keeps all its lanes. Lane history is not kept.
    /// </summary>
    private static bool WasOnRaft(Lane lane, DateOnly date, CultivationCycle? cycle) =>
        BusinessClock.ToBusinessDate(lane.CreatedAt) <= date || (cycle is not null && lane.CreatedAt <= cycle.CreatedAt);
}
