using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>The lane states of a raft on a date and the cycle they come from.</summary>
/// <param name="Cycle">The cycle open on the date, or null when every lane is Not seeded.</param>
/// <param name="LanesWithRecords">The lanes with a record in that cycle.</param>
public sealed record LaneDay(CycleSpan? Cycle, IReadOnlyDictionary<Guid, LaneState> States, IReadOnlySet<Guid> LanesWithRecords);

/// <summary>
/// Reads the records <see cref="LaneStateRules"/> works from: the seeding sessions, and H1a feeds the harvest lots here, in this one place. Until they exist no lane
/// is in a lot, so a seeded lane is Growing.
/// </summary>
public sealed class LaneStateReader(ICultivationCycleQuery cycles, ISeedingRecordRepository seedingRecords)
{
    private static readonly LaneHarvest[] NoHarvests = [];

    public async Task<LaneDay> ReadAsync(Raft raft, DateOnly date, CancellationToken cancellationToken)
    {
        var cycle = await cycles.CycleOnDateAsync(raft.Id, date, cancellationToken);
        var seedings = cycle is null ? [] : await seedingRecords.LaneSeedingsAsync(cycle.Id, cancellationToken);
        return new LaneDay(
            cycle,
            LaneStateRules.StatesOn(date, cycle, raft.Lanes.Select(l => l.Id), seedings, NoHarvests),
            LaneStateRules.LanesWithRecords(cycle, seedings, NoHarvests));
    }
}
