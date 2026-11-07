using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Cultivation.Seeding;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>BR-CYCLE-02 on the state of a lane, and the texts of MSG-CYCLE-01 to 03 as Part V of the SRS has them.</summary>
public class SeedingLaneRulesTests
{
    [Theory]
    [InlineData(LaneState.NotSeeded, false, true)]
    [InlineData(LaneState.Growing, false, true)]
    [InlineData(LaneState.BeingHarvested, false, false)]
    [InlineData(LaneState.Harvested, false, false)]
    [InlineData(LaneState.NotSeeded, true, true)]
    [InlineData(LaneState.Growing, true, false)]
    [InlineData(LaneState.BeingHarvested, true, false)]
    [InlineData(LaneState.Harvested, true, false)]
    public void A_lane_can_be_seeded_as_br_cycle_02_says(LaneState state, bool seedingCompleted, bool allowed) =>
        Assert.Equal(allowed, SeedingLaneRules.CanBeSeeded(state, seedingCompleted));

    [Theory]
    [InlineData(LaneState.NotSeeded, false)]
    [InlineData(LaneState.Growing, false)]
    [InlineData(LaneState.BeingHarvested, true)]
    [InlineData(LaneState.Harvested, true)]
    public void A_session_with_a_lane_in_a_harvest_lot_goes_to_trace_03(LaneState state, bool inALot) =>
        Assert.Equal(inALot, SeedingLaneRules.IsInAHarvestLot(state));

    [Fact]
    public void Msg_cycle_01_has_its_second_sentence_only_when_some_lanes_have_no_seed()
    {
        var some = UserMessages.FinishSeedingQuestion("KV01-B01", [3, 5]);
        var none = UserMessages.FinishSeedingQuestion("KV01-B01", []);

        Assert.Equal("MSG-CYCLE-01", some.Code);
        Assert.Equal("Finish seeding of raft KV01-B01? 2 lane(s) have no seed yet (lanes 3, 5); you can still seed them later. The Cooperative Owner will be asked to review the seeding.", some.Text);
        Assert.Equal("Finish seeding of raft KV01-B01? The Cooperative Owner will be asked to review the seeding.", none.Text);
    }

    [Fact]
    public void Msg_cycle_02_and_03_read_as_the_srs_has_them()
    {
        var finished = UserMessages.SeedingFinished("KV01-B01", 3, 5);

        Assert.Equal(("MSG-CYCLE-02", "Raft KV01-B01: seeding is finished; 3 of 5 lanes have seed. Please review the seeding sessions."), (finished.Code, finished.Text));
        Assert.Equal(
            ("MSG-CYCLE-03", "There is no seed lot to choose: no completed purchase receipt for seed exists yet. Please complete the purchase receipt for the seed first."),
            (UserMessages.NoSeedLot.Code, UserMessages.NoSeedLot.Text));
    }
}
