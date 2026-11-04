using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>CYCLE-01 and CYCLE-05 cycle reads, for a Zone Manager of the raft's zone and the Cooperative Owner; a Discontinued raft keeps its history.</summary>
public sealed class CultivationCycleReadService(
    ICultivationCycleRepository cycles,
    IRaftRepository rafts,
    IFarmingZoneRepository zones,
    CultivationCycleViews views,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    public async Task<Result<CycleView>> GetAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        return await CanViewAsync(cycle.Raft.ZoneId, cancellationToken) ? await views.ViewAsync(cycle, cancellationToken) : AppError.Forbidden;
    }

    /// <summary>The cycles of the raft with their start and close dates, the newest first (CYCLE-05.1).</summary>
    public async Task<Result<IReadOnlyList<CycleListItem>>> ListOfRaftAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await CanViewAsync(raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return (await cycles.ListByRaftAsync(raftId, cancellationToken)).Select(views.ListItem).ToList();
    }

    /// <summary>SCR-CYCLE-01: every raft of the zone by code, with its Open or last closed cycle and the commands allowed. The route has checked the zone.</summary>
    public async Task<Result<IReadOnlyList<ZoneCycleItem>>> ListOfZoneAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var found = await zones.GetByIdsAsync([zoneId], cancellationToken);
        if (found.Count == 0)
        {
            return AppError.NotFound;
        }

        var ofZone = (await cycles.ListByZoneAsync(zoneId, cancellationToken)).ToLookup(c => c.RaftId);
        var raftsOfZone = await rafts.ListByZoneAsync(zoneId, null, cancellationToken);
        var shown = raftsOfZone.ToDictionary(r => r.Id, r => CurrentOf(ofZone[r.Id]));
        var withRecords = await cycles.WithRecordsAsync(shown.Values.OfType<CultivationCycle>().Select(c => c.Id).ToList(), cancellationToken);
        var zoneInUse = found[0].StatusId == StatusIds.Usage.InUse;
        var today = timeProvider.Today();

        return raftsOfZone.Select(raft =>
        {
            var cycle = shown[raft.Id];
            var lastClose = ofZone[raft.Id].Max(c => c.CloseDate);
            var noOpenCycle = cycle?.StatusId != StatusIds.CultivationCycle.Open;
            return new ZoneCycleItem(
                raft.Id,
                raft.Code,
                raft.StatusId,
                statusCatalog.DisplayNameOf(raft.StatusId),
                RaftLaneChange.InUseCount(raft.Lanes),
                cycle is null ? null : views.ListItem(cycle),
                lastClose,
                noOpenCycle && zoneInUse && raft.StatusId == StatusIds.Usage.InUse && (lastClose is null || lastClose < today),
                CultivationCycleViews.CanEdit(cycle, raft),
                CultivationCycleViews.CanCancel(cycle, raft, cycle is not null && withRecords.Contains(cycle.Id)));
        }).ToList();
    }

    /// <summary>The Open cycle, else the one closed last.</summary>
    private static CultivationCycle? CurrentOf(IEnumerable<CultivationCycle> ofRaft) =>
        ofRaft.OrderByDescending(c => c.StatusId == StatusIds.CultivationCycle.Open).ThenByDescending(c => c.CloseDate).FirstOrDefault();

    private Task<bool> CanViewAsync(Guid zoneId, CancellationToken cancellationToken) =>
        zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, zoneId, cancellationToken);
}
