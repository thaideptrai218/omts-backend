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
    /// <summary>The cycle with its raft loaded. Harvest lots come with H1a: until then no cycle has a last lot.</summary>
    public async Task<CycleView> ViewAsync(CultivationCycle cycle, CancellationToken cancellationToken)
    {
        var hasRecords = (await cycles.WithRecordsAsync([cycle.Id], cancellationToken)).Count > 0;
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
            CanEdit(cycle, cycle.Raft),
            CanCancel(cycle, cycle.Raft, hasRecords),
            RowVersionGuard.Encode(cycle.RowVersion));
    }

    public CycleListItem ListItem(CultivationCycle cycle) => new(
        cycle.Id, cycle.StartDate, cycle.CloseDate, cycle.StatusId, statusCatalog.DisplayNameOf(cycle.StatusId), RowVersionGuard.Encode(cycle.RowVersion));

    /// <summary>CYCLE-01 PRE-3.</summary>
    public static bool CanEdit(CultivationCycle? cycle, Raft raft) =>
        cycle?.StatusId == StatusIds.CultivationCycle.Open && raft.StatusId == StatusIds.Usage.InUse;

    /// <summary>CYCLE-01 PRE-3.</summary>
    public static bool CanCancel(CultivationCycle? cycle, Raft raft, bool hasRecords) => CanEdit(cycle, raft) && !hasRecords;
}
