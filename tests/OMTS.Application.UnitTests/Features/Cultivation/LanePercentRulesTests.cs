using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>Owner decision of 12/11/2026: the percentages of a lane in a cycle, the manual change that wins over what came before it, and the state that follows.</summary>
public sealed class LanePercentRulesTests
{
    private static readonly Guid Cycle = Guid.NewGuid();
    private static readonly Guid Lane = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static LaneSeeding Seed(decimal percent, int minutes) => new(Cycle, Lane, Day, percent, T0.AddMinutes(minutes), Guid.NewGuid());

    private static LaneHarvest Lot(decimal? percent, int minutes, bool finished = false) => new(Cycle, Lane, Day, finished, percent, T0.AddMinutes(minutes), Guid.NewGuid());

    private static LaneProgressStep Step(decimal seeded, decimal? harvested, int minutes) =>
        new(Cycle, Lane, T0.AddMinutes(minutes), Day, Guid.NewGuid(), "recount", 0m, null, seeded, harvested);

    private static LanePercents Of(IReadOnlyCollection<LaneSeeding> seeds, IReadOnlyCollection<LaneHarvest> lots, params LaneProgressStep[] steps) =>
        LanePercentRules.Of(Lane, Day, seeds, lots, steps);

    [Fact]
    public void Sessions_add_up_and_a_lane_with_none_holds_nothing()
    {
        Assert.Equal(new LanePercents(100m, 0m), Of([Seed(60m, 0), Seed(40m, 1)], []));
        Assert.Equal(new LanePercents(0m, 0m), Of([], []));
    }

    [Fact]
    public void Two_whole_sessions_never_add_above_100()
    {
        Assert.Equal(100m, Of([Seed(100m, 0), Seed(100m, 1)], []).Seeded);
    }

    [Fact]
    public void A_lot_with_no_share_leaves_the_harvest_unknown_and_a_finished_lot_makes_it_100()
    {
        Assert.Null(Of([Seed(100m, 0)], [Lot(null, 1)]).Harvested);
        Assert.Equal(100m, Of([Seed(100m, 0)], [Lot(null, 1), Lot(30m, 2, finished: true)]).Harvested);
    }

    [Fact]
    public void Shares_of_lots_add_up_to_the_harvested_percent()
    {
        Assert.Equal(70m, Of([Seed(100m, 0)], [Lot(30m, 1), Lot(40m, 2)]).Harvested);
    }

    [Fact]
    public void A_manual_change_wins_over_what_was_recorded_before_it_and_what_comes_after_adds_to_it()
    {
        var seeds = new[] { Seed(100m, 0) };
        var lots = new[] { Lot(null, 1, finished: true), Lot(20m, 20) };

        var percents = Of(seeds, lots, Step(80m, 50m, 10));

        Assert.Equal(new LanePercents(80m, 70m), percents);
    }

    [Fact]
    public void A_manual_change_that_left_the_harvest_unknown_keeps_it_unknown()
    {
        Assert.Null(Of([Seed(100m, 0)], [Lot(null, 1)], Step(60m, null, 5)).Harvested);
        Assert.Equal(60m, Of([Seed(100m, 0)], [Lot(null, 1)], Step(60m, null, 5)).Seeded);
    }

    [Fact]
    public void The_session_being_corrected_is_left_out_so_it_is_not_counted_against_itself()
    {
        var mine = Seed(60m, 0);
        var other = Seed(30m, 1);

        Assert.Equal(30m, LanePercentRules.Of(Lane, Day, [mine, other], [], [], mine.RecordId).Seeded);
    }

    [Fact]
    public void The_state_follows_the_percentages()
    {
        Assert.Equal(LaneState.NotSeeded, LanePercentRules.StateOf(new LanePercents(0m, 0m), false));
        Assert.Equal(LaneState.Growing, LanePercentRules.StateOf(new LanePercents(40m, 0m), false));
        Assert.Equal(LaneState.BeingHarvested, LanePercentRules.StateOf(new LanePercents(100m, 30m), true));
        Assert.Equal(LaneState.BeingHarvested, LanePercentRules.StateOf(new LanePercents(100m, null), true));
        Assert.Equal(LaneState.Growing, LanePercentRules.StateOf(new LanePercents(100m, null), false));
        Assert.Equal(LaneState.Harvested, LanePercentRules.StateOf(new LanePercents(100m, 100m), true));
        Assert.Equal(LaneState.NotSeeded, LanePercentRules.StateOf(new LanePercents(0m, 0m), true));
    }

    [Fact]
    public void What_can_still_be_added_is_100_less_the_total_and_never_below_zero()
    {
        Assert.Equal(40m, LanePercentRules.Remaining(60m));
        Assert.Equal(100m, LanePercentRules.Remaining(null));
        Assert.Equal(0m, LanePercentRules.Remaining(100m));
    }
}
