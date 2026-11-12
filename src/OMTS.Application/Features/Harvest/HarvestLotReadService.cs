using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Harvest;

/// <summary>HARVEST-01 reads, for a Zone Manager of the raft's zone and the Cooperative Owner. Viewing changes nothing.</summary>
public sealed class HarvestLotReadService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    IHarvestLotRepository lots,
    ICooperativeRepository cooperatives,
    LaneStateReader laneStates,
    HarvestLotViews views,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    /// <summary>HARVEST-01.0 step 1: the lanes with their state today and the lots of the Open cycle. With no Open cycle: the lanes, no lot, and nothing to record.</summary>
    public async Task<Result<HarvestLotsScreen>> ScreenAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.View, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var day = await laneStates.ReadAsync(raft, timeProvider.Today(), cancellationToken);
        var span = await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken);
        var open = span is null ? null : await cycles.GetAsync(span.Id, cancellationToken);
        var mayRecord = open is not null
            && raft.StatusId == StatusIds.Usage.InUse && raft.Zone.StatusId == StatusIds.Usage.InUse
            && await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.Create, raft.ZoneId, cancellationToken);
        var lanes = raft.Lanes
            .Where(l => l.StatusId == StatusIds.Lane.InUse || day.LanesWithRecords.Contains(l.Id))
            .OrderBy(l => l.LaneNumber)
            .Select(l => new HarvestLaneView(
                l.Id, l.LaneNumber, l.StatusId, statusCatalog.DisplayNameOf(l.StatusId), day.States[l.Id],
                mayRecord && l.StatusId == StatusIds.Lane.InUse && LaneStateRules.CanBeHarvested(day.States[l.Id])))
            .ToList();
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("A harvest lot needs the cooperative created at deployment.");

        return new HarvestLotsScreen(
            raft.Id,
            raft.Code,
            open is null ? null : new CycleListItem(open.Id, open.StartDate, open.CloseDate, open.StatusId, statusCatalog.DisplayNameOf(open.StatusId), RowVersionGuard.Encode(open.RowVersion), open.ClosedEarly, CultivationCycleViews.ProductOf(open)),
            cooperative.AverageKgPerCage,
            lanes.Any(l => l.CanHarvest),
            lanes,
            open is null ? [] : await views.ListAsync(await lots.ListByCycleAsync(open.Id, cancellationToken), open, raft, cancellationToken));
    }

    public async Task<Result<HarvestLotView>> GetAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.View, lot.Raft.ZoneId, cancellationToken)
            ? await views.ViewAsync(lot, lot.Raft, cancellationToken)
            : AppError.Forbidden;
    }
}
