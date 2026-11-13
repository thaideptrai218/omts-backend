using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The lane put out of service (decision of 12/11/2026) in the lane states and in the rule for closing a cycle normally.</summary>
public class LaneStateRulesOutOfServiceTests
{
    private static readonly DateOnly Day = new(2026, 11, 12);

    private static Lane LaneOf(DateOnly? outOfServiceOn = null) => new() { LaneNumber = 1, OutOfServiceOn = outOfServiceOn };

    private static LaneState StateOf(Lane lane, LaneState before, DateOnly date) =>
        LaneStateRules.WithOutOfService(new Dictionary<Guid, LaneState> { [lane.Id] = before }, [lane], date)[lane.Id];

    [Theory]
    [InlineData(LaneState.Growing, LaneState.OutOfService)]
    [InlineData(LaneState.BeingHarvested, LaneState.OutOfService)]
    [InlineData(LaneState.Harvested, LaneState.Harvested)] // a lane harvested before keeps that
    [InlineData(LaneState.NotSeeded, LaneState.NotSeeded)] // no seed to lose: the flag of the lane says it is out of service
    public void A_lane_out_of_service_that_had_seed_shows_out_of_service(LaneState before, LaneState expected) =>
        Assert.Equal(expected, StateOf(LaneOf(Day), before, Day));

    [Fact]
    public void A_date_before_the_lane_went_out_of_service_is_as_it_was_and_a_lane_in_service_is_untouched()
    {
        Assert.Equal(LaneState.Growing, StateOf(LaneOf(Day), LaneState.Growing, Day.AddDays(-1)));
        Assert.Equal(LaneState.Growing, StateOf(LaneOf(), LaneState.Growing, Day));
        Assert.Equal(LaneState.OutOfService, StateOf(LaneOf(Day), LaneState.Growing, Day.AddDays(30)));
    }

    [Fact]
    public void Out_of_service_is_an_end_of_the_lane_seeding_and_harvest_are_not_possible()
    {
        Assert.False(LaneStateRules.CanBeHarvested(LaneState.OutOfService));
        Assert.False(SeedingLaneRules.CanBeSeeded(LaneState.OutOfService, seedingCompleted: false));
    }


    [Theory]
    [InlineData(new[] { LaneState.Harvested, LaneState.OutOfService }, true)] // the lane lost counts as finished
    [InlineData(new[] { LaneState.OutOfService, LaneState.OutOfService }, true)]
    [InlineData(new[] { LaneState.Harvested, LaneState.OutOfService, LaneState.NotSeeded }, true)]
    [InlineData(new[] { LaneState.OutOfService, LaneState.Growing }, false)]
    [InlineData(new[] { LaneState.OutOfService, LaneState.BeingHarvested }, false)]
    [InlineData(new[] { LaneState.NotSeeded }, false)]
    public void A_cycle_closes_normally_when_every_lane_with_seed_is_harvested_or_out_of_service(LaneState[] lanes, bool expected) =>
        Assert.Equal(expected, LaneStateRules.EverySeededLaneHarvested(lanes));

    [Fact]
    public void The_walk_seeded_lost_and_replaced_by_nothing_needs_no_renumbering_the_number_stays()
    {
        var lane = new Lane { LaneNumber = 4, OutOfServiceOn = Day };

        Assert.Equal(4, lane.LaneNumber);
        Assert.True(lane.IsOutOfService);
        Assert.False(lane.IsUsable);
        Assert.False(new Lane { IsDamaged = true }.IsUsable);
        Assert.True(new Lane().IsUsable);
    }
}
