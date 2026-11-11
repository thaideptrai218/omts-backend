using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>The lane states of a raft on a date and the cycle they come from.</summary>
/// <param name="Cycle">The cycle open on the date, or null when every lane is Not seeded.</param>
/// <param name="LanesWithRecords">The lanes with a record in that cycle.</param>
public sealed record LaneDay(CycleSpan? Cycle, IReadOnlyDictionary<Guid, LaneState> States, IReadOnlySet<Guid> LanesWithRecords);

/// <summary>
/// Reads the records <see cref="LaneStateRules"/> works from, in this one place: the seeding sessions and the harvest lots of the cycle.
/// </summary>
public sealed class LaneStateReader(ICultivationCycleQuery cycles, ISeedingRecordRepository seedingRecords, IHarvestLotRepository harvestLots)
{
    public async Task<LaneDay> ReadAsync(Raft raft, DateOnly date, CancellationToken cancellationToken)
    {
        var cycle = await cycles.CycleOnDateAsync(raft.Id, date, cancellationToken);
        var seedings = cycle is null ? [] : await seedingRecords.LaneSeedingsAsync(cycle.Id, cancellationToken);
        var harvests = cycle is null ? [] : await harvestLots.LaneHarvestsAsync(cycle.Id, cancellationToken);
        var states = LaneStateRules.StatesOn(date, cycle, raft.Lanes.Select(l => l.Id), seedings, harvests);
        return new LaneDay(cycle, LaneStateRules.WithOutOfService(states, raft.Lanes, date), LaneStateRules.LanesWithRecords(cycle, seedings, harvests));
    }
}
