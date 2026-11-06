using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>BR-FARM-03 and the AC-03 walk: lane 4 of KV01-B01 harvested on 26/10 without being finished, and on 27/10 when it is marked finished.</summary>
public class LaneStateRulesTests
{
    private static readonly Guid Lane4 = Guid.NewGuid();
    private static readonly Guid Lane5 = Guid.NewGuid();
    private static readonly CycleSpan Cycle = new(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), null);
    private static readonly LaneSeeding Seeded = new(Cycle.Id, Lane4, new DateOnly(2026, 3, 5));
    private static readonly LaneHarvest FirstLot = new(Cycle.Id, Lane4, new DateOnly(2026, 10, 26), IsFinished: false);
    private static readonly LaneHarvest SecondLot = new(Cycle.Id, Lane4, new DateOnly(2026, 10, 27), IsFinished: true);

    private static LaneState Lane4On(DateOnly date, CycleSpan? cycle = null, LaneSeeding[]? seedings = null, LaneHarvest[]? harvests = null) =>
        LaneStateRules.StatesOn(date, cycle ?? Cycle, [Lane4], seedings ?? [Seeded], harvests ?? [FirstLot, SecondLot])[Lane4];

    [Theory]
    [InlineData(2026, 3, 1, LaneState.NotSeeded)] // the cycle has started, nothing is hung yet
    [InlineData(2026, 3, 4, LaneState.NotSeeded)]
    [InlineData(2026, 3, 5, LaneState.Growing)] // the seeding day
    [InlineData(2026, 10, 25, LaneState.Growing)]
    [InlineData(2026, 10, 26, LaneState.BeingHarvested)] // AC-03: after the first lot, not finished
    [InlineData(2026, 10, 27, LaneState.Harvested)] // AC-03: the second lot marks it finished
    [InlineData(2026, 10, 28, LaneState.Harvested)]
    public void Ac03_the_lane_moves_through_every_state_by_date(int year, int month, int day, LaneState expected) =>
        Assert.Equal(expected, Lane4On(new DateOnly(year, month, day)));

    [Fact]
    public void Ac03_a_lane_is_choosable_for_harvest_until_it_is_finished_and_never_before_it_has_seed()
    {
        Assert.False(LaneStateRules.CanBeHarvested(Lane4On(new DateOnly(2026, 3, 4))));
        Assert.True(LaneStateRules.CanBeHarvested(Lane4On(new DateOnly(2026, 10, 25))));
        Assert.True(LaneStateRules.CanBeHarvested(Lane4On(new DateOnly(2026, 10, 26))));
        Assert.False(LaneStateRules.CanBeHarvested(Lane4On(new DateOnly(2026, 10, 27))));
    }

    [Fact]
    public void A_later_finish_does_not_reach_back_and_a_lot_after_the_finish_changes_nothing()
    {
        Assert.Equal(LaneState.BeingHarvested, Lane4On(new DateOnly(2026, 10, 26), harvests: [SecondLot, FirstLot]));
        var finishedFirst = new[] { FirstLot with { IsFinished = true }, SecondLot with { IsFinished = false } };
        Assert.Equal(LaneState.Harvested, Lane4On(new DateOnly(2026, 10, 27), harvests: finishedFirst));
    }

    [Fact]
    public void Each_lane_has_its_own_state()
    {
        var states = LaneStateRules.StatesOn(new DateOnly(2026, 10, 26), Cycle, [Lane4, Lane5], [Seeded, Seeded with { LaneId = Lane5 }], [FirstLot]);

        Assert.Equal(LaneState.BeingHarvested, states[Lane4]);
        Assert.Equal(LaneState.Growing, states[Lane5]);
    }

    // Re-hanging: a second seeding of a Growing lane leaves it Growing (BR-CYCLE-02).
    [Fact]
    public void A_lane_seeded_again_stays_growing()
    {
        Assert.Equal(LaneState.Growing, Lane4On(new DateOnly(2026, 4, 1), seedings: [Seeded, Seeded with { Date = new DateOnly(2026, 3, 20) }], harvests: []));
    }

    [Fact]
    public void With_no_cycle_every_lane_is_not_seeded()
    {
        var states = LaneStateRules.StatesOn(new DateOnly(2026, 10, 26), null, [Lane4, Lane5], [Seeded], [FirstLot]);

        Assert.All(states.Values, state => Assert.Equal(LaneState.NotSeeded, state));
    }

    // CYCLE-05.1: the close day still belongs to the closed cycle; the day after it no cycle is open.
    [Fact]
    public void The_close_day_belongs_to_the_cycle_and_the_day_after_it_does_not()
    {
        var closed = Cycle with { CloseDate = new DateOnly(2026, 10, 27) };

        Assert.Equal(LaneState.Harvested, Lane4On(new DateOnly(2026, 10, 27), closed));
        Assert.Equal(LaneState.NotSeeded, Lane4On(new DateOnly(2026, 10, 28), closed));
    }

    [Fact]
    public void A_date_before_the_start_of_the_cycle_is_not_seeded()
    {
        Assert.Equal(LaneState.NotSeeded, Lane4On(new DateOnly(2026, 2, 28), harvests: [], seedings: [Seeded with { Date = new DateOnly(2026, 2, 1) }]));
    }

    // A new cycle starts over: what the previous cycle did to the lane does not count.
    [Fact]
    public void Records_of_another_cycle_do_not_count()
    {
        var next = new CycleSpan(Guid.NewGuid(), Cycle.RaftId, new DateOnly(2026, 11, 1), null);

        Assert.Equal(LaneState.NotSeeded, Lane4On(new DateOnly(2026, 11, 2), next));
    }

    [Fact]
    public void A_cycle_is_open_from_its_start_to_its_close_both_included()
    {
        var closed = Cycle with { CloseDate = new DateOnly(2026, 3, 3) };

        Assert.False(closed.IsOpenOn(new DateOnly(2026, 2, 28)));
        Assert.True(closed.IsOpenOn(new DateOnly(2026, 3, 1)));
        Assert.True(closed.IsOpenOn(new DateOnly(2026, 3, 3)));
        Assert.False(closed.IsOpenOn(new DateOnly(2026, 3, 4)));
        Assert.True(Cycle.IsOpenOn(new DateOnly(2030, 1, 1)));
    }

    [Fact]
    public void Lanes_with_records_are_those_of_this_cycle_only()
    {
        var other = Guid.NewGuid();

        var lanes = LaneStateRules.LanesWithRecords(Cycle, [Seeded, new LaneSeeding(other, Lane5, Seeded.Date)], [new LaneHarvest(Cycle.Id, Lane5, FirstLot.Date, false)]);

        Assert.Equal(new[] { Lane4, Lane5 }.ToHashSet(), lanes);
        Assert.Empty(LaneStateRules.LanesWithRecords(Cycle, [new LaneSeeding(other, Lane5, Seeded.Date)], []));
        Assert.Empty(LaneStateRules.LanesWithRecords(null, [Seeded], []));
    }
}
