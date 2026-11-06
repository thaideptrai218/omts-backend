using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>The cycle answers, with the figures and commands worked out when shown.</summary>
public sealed class CultivationCycleViews(ICultivationCycleRepository cycles, IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    /// <summary>The cycle with its raft and zone loaded. Harvest lots come with H1a: until then no cycle has a last lot.</summary>
    public async Task<CycleView> ViewAsync(CultivationCycle cycle, CancellationToken cancellationToken)
    {
        var hasRecords = (await cycles.WithRecordsAsync([cycle.Id], cancellationToken)).Count > 0;
        var zoneInUse = cycle.Raft.Zone.StatusId == StatusIds.Usage.InUse;
        return new CycleView(
            cycle.Id,
            cycle.RaftId,
            cycle.Raft.Code,
            cycle.StartDate,
            cycle.CloseDate,
            cycle.ExpectedSeedQuantity,
            cycle.ExpectedHarvestDate,
            cycle.TargetYieldKg,
            cycle.SeedingCompletedOn,
            cycle.Assessment,
            cycle.ClosedEarly,
            CycleDaysOfFarming.Count(cycle.StartDate, null, cycle.CloseDate, timeProvider.Today()),
            cycle.StatusId,
            statusCatalog.DisplayNameOf(cycle.StatusId),
            CanEdit(cycle, cycle.Raft, zoneInUse),
            CanCancel(cycle, cycle.Raft, zoneInUse, hasRecords),
            RowVersionGuard.Encode(cycle.RowVersion));
    }

    public CycleListItem ListItem(CultivationCycle cycle) => new(
        cycle.Id, cycle.StartDate, cycle.CloseDate, cycle.StatusId, statusCatalog.DisplayNameOf(cycle.StatusId), RowVersionGuard.Encode(cycle.RowVersion));

    /// <summary>CYCLE-01 PRE-3, as the writes answer it: an Open cycle, with neither its raft nor its zone Discontinued.</summary>
    public static bool CanEdit(CultivationCycle? cycle, Raft raft, bool zoneInUse) =>
        cycle?.StatusId == StatusIds.CultivationCycle.Open && raft.StatusId == StatusIds.Usage.InUse && zoneInUse;

    /// <summary>CYCLE-01 PRE-3.</summary>
    public static bool CanCancel(CultivationCycle? cycle, Raft raft, bool zoneInUse, bool hasRecords) => CanEdit(cycle, raft, zoneInUse) && !hasRecords;
}
