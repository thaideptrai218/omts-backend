using OMTS.Application.Common.Results;
using OMTS.Application.Features.Harvest;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>PM decision 11/11/2026: fractional cages, kg from cages, the 0.2x to 2x range, and the cap of the cycle's harvested kg against its target yield.</summary>
public class HarvestQuantityRulesTests
{
    [Theory]
    [InlineData("0.5", true)]
    [InlineData("0.01", true)]
    [InlineData("12", true)]
    [InlineData("100000", true)]
    [InlineData("100000.01", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1.005", false)]
    public void Cages_are_above_0_at_most_100000_with_2_decimals(string cages, bool valid) =>
        Assert.Equal(valid, HarvestQuantityRules.CagesAreValid(decimal.Parse(cages, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Cages_are_missing_when_null() => Assert.False(HarvestQuantityRules.CagesAreValid(null));

    [Theory]
    [InlineData(8, 50, 400)]
    [InlineData(0.5, 50, 25)]
    [InlineData(2.25, 50.5, 113.63)] // 113.625 rounds away from zero to 2 decimals
    public void Kg_is_cages_times_kg_per_cage(decimal cages, decimal perCage, decimal kg) => Assert.Equal(kg, HarvestQuantityRules.KgOf(cages, perCage));

    [Theory]
    [InlineData(100, true)]
    [InlineData(20, true)] // 0.2 x 100
    [InlineData(200, true)] // 2 x 100
    [InlineData(19.99, false)]
    [InlineData(200.01, false)]
    public void A_kg_sent_by_hand_stays_within_02x_to_2x_of_cages_times_kg_per_cage(decimal kg, bool near) =>
        Assert.Equal(near, HarvestQuantityRules.KgIsNearCages(kg, 2m, 50m));

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1000.01, false)]
    [InlineData(0.01, true)]
    public void Only_a_kg_above_ten_times_the_worked_kg_is_refused(double kg, bool accepted) =>
        Assert.Equal(accepted, HarvestQuantityRules.KgIsWithinCeiling((decimal)kg, 2m, 50m));

    [Fact]
    public void A_kg_far_from_the_worked_kg_gives_a_warning_and_a_near_one_none()
    {
        Assert.Empty(HarvestQuantityRules.KgWarnings(2m, 50m, 100m));
        Assert.Equal(HarvestQuantityRules.KgFarFromCagesCode, Assert.Single(HarvestQuantityRules.KgWarnings(2m, 50m, 19m)).Code);
    }

    [Fact]
    public void Without_a_target_there_is_no_cap()
    {
        Assert.Null(HarvestQuantityRules.CheckPlan(new HarvestPlanInput(null, 5_000_000m, false, null), 9_000_000m, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(0, 1000)] // exactly the target
    [InlineData(500, 600)] // 1100 = target + 10 %
    public void Up_to_110_percent_of_the_target_needs_nothing(decimal harvested, decimal added) =>
        Assert.Null(HarvestQuantityRules.CheckPlan(new HarvestPlanInput(1000m, harvested, false, null), added, out _));

    [Fact]
    public void Between_110_and_150_percent_asks_for_a_confirmation_with_the_figures()
    {
        var error = HarvestQuantityRules.CheckPlan(new HarvestPlanInput(1000m, 800m, false, null), 400m, out _)!;

        Assert.Equal(ErrorKind.Conflict, error.Kind);
        Assert.Equal("MSG-GEN-04", error.Message.Code);
        Assert.Equal("confirmOverPlan", error.Details!["confirmFlag"]);
        Assert.Equal(1200m, error.Details["totalKg"]);
        Assert.Equal(20m, error.Details["overPlanPercent"]);
    }

    [Fact]
    public void A_confirmation_needs_a_reason_and_keeps_it_trimmed()
    {
        var withoutReason = HarvestQuantityRules.CheckPlan(new HarvestPlanInput(1000m, 800m, true, "  "), 400m, out _)!;
        Assert.Equal((ErrorKind.Validation, "Over plan reason"), (withoutReason.Kind, withoutReason.Details!["field"]));

        Assert.Null(HarvestQuantityRules.CheckPlan(new HarvestPlanInput(1000m, 800m, true, "  late season  "), 400m, out var reason));
        Assert.Equal("late season", reason);
    }

    [Fact]
    public void Above_150_percent_is_refused_even_when_confirmed()
    {
        var error = HarvestQuantityRules.CheckPlan(new HarvestPlanInput(1000m, 1000m, true, "because"), 501m, out _)!;

        Assert.Equal((ErrorKind.Validation, "Estimated kg"), (error.Kind, error.Details!["field"]));
    }

    [Fact]
    public void Exactly_150_percent_is_still_a_warning_not_a_refusal() =>
        Assert.Equal(ErrorKind.Conflict, HarvestQuantityRules.CheckPlan(new HarvestPlanInput(1000m, 1000m, false, null), 500m, out _)!.Kind);
}
