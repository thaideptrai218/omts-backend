using OMTS.Application.Common.Results;
using OMTS.Application.Features.Harvest;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>PM decision 11/11/2026: fractional cages, kg from cages, the 0.2x to 2x range, and the share of the target yield reached (a plan, not a cap: owner decision 12/11/2026).</summary>
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

    [Theory]
    [InlineData(1000, 700, 70)]
    [InlineData(500, 700, 140)] // above the target is only a figure
    [InlineData(3, 1, 33.3)]
    public void The_target_percent_is_the_harvested_kg_over_the_target_and_may_exceed_100(decimal target, decimal harvested, decimal percent) =>
        Assert.Equal(percent, HarvestQuantityRules.TargetPercent(target, harvested));

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void Without_a_target_above_0_there_is_no_percent(double? target) =>
        Assert.Null(HarvestQuantityRules.TargetPercent((decimal?)target, 500m));
}
