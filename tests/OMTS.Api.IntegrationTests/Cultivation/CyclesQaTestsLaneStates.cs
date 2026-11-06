using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of <see cref="LaneStateRules"/> (BR-FARM-03) walking AC-03 day by day, as far as C1 can, and of the days of farming (PM 03/11).</summary>
public class CyclesQaTestsLaneStates
{
    private static readonly Guid Lane4 = Guid.NewGuid();
    private static readonly Guid Lane5 = Guid.NewGuid();
    private static readonly CycleSpan Open = new(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1), null);

    private static DateOnly October(int day) => new(2026, 10, day);

    private static LaneState StateOfLane4(DateOnly date, CycleSpan? cycle, LaneSeeding[] seedings, LaneHarvest[] harvests) =>
        LaneStateRules.StatesOn(date, cycle, [Lane4, Lane5], seedings, harvests)[Lane4];

    // AC-03: lane 4 is in the lots of 26/10 (not finished) and 27/10 (finished).
    [Theory]
    [InlineData(9, LaneState.NotSeeded)]
    [InlineData(10, LaneState.Growing)]
    [InlineData(25, LaneState.Growing)]
    [InlineData(26, LaneState.BeingHarvested)]
    [InlineData(27, LaneState.Harvested)]
    [InlineData(31, LaneState.Harvested)]
    public void Lane_4_of_ac_03_walks_through_the_four_states(int day, LaneState expected)
    {
        LaneSeeding[] seedings = [new(Open.Id, Lane4, October(10))];
        LaneHarvest[] harvests = [new(Open.Id, Lane4, October(26), false), new(Open.Id, Lane4, October(27), true)];

        Assert.Equal(expected, StateOfLane4(October(day), Open, seedings, harvests));
    }

    [Theory]
    [InlineData(LaneState.NotSeeded, false)]
    [InlineData(LaneState.Growing, true)]
    [InlineData(LaneState.BeingHarvested, true)]
    [InlineData(LaneState.Harvested, false)]
    public void Only_a_growing_or_being_harvested_lane_can_be_chosen_in_a_lot(LaneState state, bool allowed) =>
        Assert.Equal(allowed, LaneStateRules.CanBeHarvested(state));

    [Fact]
    public void The_order_of_the_lots_does_not_matter_and_a_lane_with_no_record_stays_not_seeded()
    {
        LaneSeeding[] seedings = [new(Open.Id, Lane4, October(10)), new(Open.Id, Lane4, October(12))];
        LaneHarvest[] harvests = [new(Open.Id, Lane4, October(27), true), new(Open.Id, Lane4, October(26), false)];

        var states = LaneStateRules.StatesOn(October(28), Open, [Lane4, Lane5], seedings, harvests);

        Assert.Equal((LaneState.Harvested, LaneState.NotSeeded), (states[Lane4], states[Lane5]));
    }

    // BR-FARM-03: from the records of the cycle open on that date; none open, every lane is Not seeded. The close day belongs to the cycle.
    [Fact]
    public void A_date_before_the_cycle_after_its_close_or_with_no_cycle_is_not_seeded_and_other_cycles_do_not_count()
    {
        var closed = new CycleSpan(Guid.NewGuid(), Open.RaftId, October(1), October(20));
        var next = new CycleSpan(Guid.NewGuid(), Open.RaftId, October(21), null);
        LaneSeeding[] seedings = [new(closed.Id, Lane4, October(5))];
        LaneHarvest[] harvests = [new(closed.Id, Lane4, October(19), true)];

        Assert.Equal(LaneState.Harvested, StateOfLane4(October(20), closed, seedings, harvests));
        Assert.Equal(LaneState.NotSeeded, StateOfLane4(October(21), closed, seedings, harvests));
        Assert.Equal(LaneState.NotSeeded, StateOfLane4(new DateOnly(2026, 9, 30), closed, seedings, harvests));
        Assert.Equal(LaneState.NotSeeded, StateOfLane4(October(21), next, seedings, harvests));
        Assert.Equal(LaneState.NotSeeded, StateOfLane4(October(10), null, seedings, harvests));
    }

    // BR-FARM-04: a Removed lane keeps its records, so it stays on the view of that cycle only.
    [Fact]
    public void The_lanes_with_records_are_those_of_the_cycle_shown()
    {
        var other = new CycleSpan(Guid.NewGuid(), Open.RaftId, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1));
        LaneSeeding[] seedings = [new(Open.Id, Lane4, October(5)), new(other.Id, Lane5, new DateOnly(2026, 1, 5))];
        LaneHarvest[] harvests = [new(other.Id, Lane4, new DateOnly(2026, 1, 6), true)];

        Assert.Equal([Lane4], LaneStateRules.LanesWithRecords(Open, seedings, harvests));
        Assert.Equal(new HashSet<Guid> { Lane4, Lane5 }, LaneStateRules.LanesWithRecords(other, seedings, harvests));
        Assert.Empty(LaneStateRules.LanesWithRecords(null, seedings, harvests));
    }

    // BR-CYCLE-06 with PM 03/11. C5 relies on these: 01/10 to the last lot of 10/10 is 10 days, and with no lot the close date ends it.
    [Theory]
    [InlineData(10, 20, null, 10)]
    [InlineData(null, 20, null, 20)]
    [InlineData(null, 1, null, 1)]
    [InlineData(null, null, 5, 5)]
    [InlineData(10, null, 15, 15)]
    public void The_days_of_farming_count_both_ends_and_an_open_cycle_ends_today(int? lastLotDay, int? closeDay, int? todayDay, int expected)
    {
        var today = todayDay is { } day ? October(day) : October(31);

        var count = CycleDaysOfFarming.Count(October(1), lastLotDay is { } lot ? October(lot) : null, closeDay is { } close ? October(close) : null, today);

        Assert.Equal(expected, count);
    }
}
