using OMTS.Application.Features.Farming.Licenses.Models;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Farming.Rafts.Models;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Farming;

public class FarmingRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    [Theory]
    [InlineData(30, LicenseState.ExpiringSoon)]
    [InlineData(31, LicenseState.Valid)]
    [InlineData(0, LicenseState.ExpiringSoon)]
    [InlineData(-1, LicenseState.Expired)]
    public void License_state_boundaries_around_the_expiry_date(int daysLeft, LicenseState expected) =>
        Assert.Equal(expected, LicenseStateRules.StateOn(new LicenseFacts(Today.AddYears(-1), Today.AddDays(daysLeft), []), Today));

    [Fact]
    public void License_without_expiry_is_valid_and_one_starting_later_is_not_yet_effective()
    {
        Assert.Equal(LicenseState.Valid, LicenseStateRules.StateOn(new LicenseFacts(Today, null, []), Today));
        Assert.Equal(LicenseState.NotYetEffective, LicenseStateRules.StateOn(new LicenseFacts(Today.AddDays(1), null, []), Today));
    }

    [Fact]
    public void Replacement_supersedes_only_from_its_effective_date()
    {
        var replaced = new LicenseFacts(Today.AddYears(-2), Today.AddDays(10), [Today.AddDays(5)]);

        Assert.Equal(LicenseState.ExpiringSoon, LicenseStateRules.StateOn(replaced, Today));
        Assert.Equal(LicenseState.Superseded, LicenseStateRules.StateOn(replaced, Today.AddDays(5)));
    }

    [Fact]
    public void Worst_state_prefers_expired_then_expiring_then_missing()
    {
        Assert.Equal(LicenseState.Expired, LicenseStateRules.Worst([LicenseState.Valid, LicenseState.Missing, LicenseState.Expired]));
        Assert.Equal(LicenseState.ExpiringSoon, LicenseStateRules.Worst([LicenseState.Missing, LicenseState.ExpiringSoon]));
        Assert.Equal(LicenseState.Missing, LicenseStateRules.Worst([LicenseState.Valid, LicenseState.Missing]));
        Assert.Null(LicenseStateRules.Worst([LicenseState.Valid, LicenseState.Superseded]));
    }

    // FARM-04 done criterion: seeded 0–40, harvested 0–10, written off 30–40 on a 50 m lane.
    [Fact]
    public void Lane_portions_follow_seeding_harvest_and_write_off()
    {
        var seeded = Segment(LaneSegmentType.Seeded, 0, 40);
        var harvested = Segment(LaneSegmentType.Harvested, 0, 10);
        var writtenOff = Segment(LaneSegmentType.WrittenOff, 30, 40);

        var portions = LaneConditionRules.Portions(50, [seeded, harvested, writtenOff]);

        Assert.Equal(
            [(0m, 10m, PortionCondition.Harvested), (10m, 30m, PortionCondition.Growing), (30m, 40m, PortionCondition.WrittenOff), (40m, 50m, PortionCondition.NotSeeded)],
            portions.Select(p => (p.From, p.To, p.Condition)));
        Assert.Equal(new[] { seeded.Id, harvested.Id }.Order(), portions[0].SegmentIds.Order());
        Assert.Equal(LaneSummary.PartiallyHarvested, LaneConditionRules.Summarize(portions));
    }

    [Fact]
    public void Planned_harvest_is_no_condition_and_overlapping_seedings_merge()
    {
        var portions = LaneConditionRules.Portions(20, [
            Segment(LaneSegmentType.Seeded, 0, 12),
            Segment(LaneSegmentType.Seeded, 8, 20),
            Segment(LaneSegmentType.PlannedHarvest, 0, 20),
        ]);

        Assert.All(portions, p => Assert.Equal(PortionCondition.Growing, p.Condition));
        Assert.Equal(0m, portions[0].From);
        Assert.Equal(20m, portions[^1].To);
        Assert.Equal(LaneSummary.Growing, LaneConditionRules.Summarize(portions));
    }

    [Fact]
    public void Lane_without_segments_is_one_not_seeded_portion_and_a_finished_lane_is_fully_harvested()
    {
        var empty = LaneConditionRules.Portions(30, []);
        var finished = LaneConditionRules.Portions(30, [
            Segment(LaneSegmentType.Seeded, 0, 30), Segment(LaneSegmentType.Harvested, 0, 20), Segment(LaneSegmentType.WrittenOff, 20, 30),
        ]);

        Assert.Equal([(0m, 30m, PortionCondition.NotSeeded)], empty.Select(p => (p.From, p.To, p.Condition)));
        Assert.Equal(LaneSummary.NotSeeded, LaneConditionRules.Summarize(empty));
        Assert.Equal(LaneSummary.FullyHarvested, LaneConditionRules.Summarize(finished));
    }

    private static SegmentFact Segment(LaneSegmentType type, decimal from, decimal to) => new(Guid.NewGuid(), type, from, to, Today);
}
