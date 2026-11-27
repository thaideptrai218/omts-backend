using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// What a raft is doing today, for the raft list and the raft detail: the lane summary and the Open cycle. It goes through <see cref="LaneStateReader"/>, so the lanes are in the states the cycle
/// progress shows, and costs a fixed number of reads however many rafts are asked for (never one per raft).
/// </summary>
public sealed class RaftLiveReader(IRaftRepository rafts, LaneStateReader laneStates, CycleSeedLabels seedLabels, TimeProvider timeProvider)
{
    /// <summary>The lane summary of each raft by id; a raft with no lane In Use maps to null, an unknown id is absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, RaftLaneSummary?>> SummariesAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken)
    {
        if (raftIds.Count == 0)
        {
            return new Dictionary<Guid, RaftLaneSummary?>();
        }

        var withLanes = await rafts.GetWithLanesByIdsAsync(raftIds, cancellationToken);
        var days = await laneStates.ReadManyAsync(withLanes, timeProvider.Today(), cancellationToken);
        return withLanes.ToDictionary(r => r.Id, r => RaftLaneSummaryRules.Of(r.Lanes, days[r.Id].States));
    }

    /// <summary>The Open cycle of a raft loaded with its lanes, or null when it has none.</summary>
    public async Task<RaftCurrentCycleDetail?> CurrentCycleAsync(Raft raft, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var day = (await laneStates.ReadManyAsync([raft], today, cancellationToken))[raft.Id];
        if (day.Cycle is not { CloseDate: null } cycle)
        {
            return null;
        }

        var names = await SeedNamesAsync(cycle.Id, cancellationToken);
        return new RaftCurrentCycleDetail(cycle.Id, cycle.StartDate, CycleDaysOfFarming.Count(cycle.StartDate, null, null, today), names, RaftLaneSummaryRules.Of(raft.Lanes, day.States));
    }

    private async Task<IReadOnlyList<string>> SeedNamesAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        if (!(await seedLabels.ReadAsync([cycleId], cancellationToken)).TryGetValue(cycleId, out var label))
        {
            return [];
        }

        return label.SeedItemNames ?? (string.IsNullOrWhiteSpace(label.SeedItemName) ? [] : [label.SeedItemName]);
    }
}
