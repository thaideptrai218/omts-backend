using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>ENV-01 alert rule: below min or above max is a violation; equal to a bound is not; an empty bound is not checked.</summary>
public class WaterThresholdEvaluatorTests
{
    private static readonly WaterThresholds Full = new(6.5m, 8.5m, 15m, 30m);

    [Theory]
    [InlineData(6.5, 15)]
    [InlineData(8.5, 30)]
    [InlineData(7.2, 22)]
    public void Values_within_or_equal_to_the_bounds_raise_no_alert(double ph, double salinity)
    {
        var result = WaterThresholdEvaluator.Evaluate((decimal)ph, (decimal)salinity, Full);

        Assert.False(result.Exceeded);
        Assert.Empty(result.ViolatedMetrics);
        Assert.Null(result.ViolatedValues);
    }

    [Fact]
    public void Each_violation_is_named_with_value_and_bound()
    {
        var result = WaterThresholdEvaluator.Evaluate(9.1m, 12m, Full);

        Assert.True(result.Exceeded);
        Assert.Equal(["pH", "Salinity"], result.ViolatedMetrics);
        Assert.Equal("pH 9.10 > 8.50; Salinity 12.00 < 15.00", result.ViolatedValues);
    }

    [Fact]
    public void Value_just_outside_a_bound_is_a_violation()
    {
        Assert.Equal("pH 6.49 < 6.50", WaterThresholdEvaluator.Evaluate(6.49m, 20m, Full).ViolatedValues);
        Assert.Equal("Salinity 30.01 > 30.00", WaterThresholdEvaluator.Evaluate(7m, 30.01m, Full).ViolatedValues);
    }

    [Fact]
    public void Only_the_bounds_that_are_set_are_checked()
    {
        var onlyPhMax = new WaterThresholds(null, 8.5m, null, null);

        Assert.False(WaterThresholdEvaluator.Evaluate(0m, 100m, onlyPhMax).Exceeded);
        var high = WaterThresholdEvaluator.Evaluate(8.6m, 0m, onlyPhMax);
        Assert.Equal(["pH"], high.ViolatedMetrics);
        Assert.Equal("pH 8.60 > 8.50", high.ViolatedValues);
    }

    [Fact]
    public void No_thresholds_means_no_alert()
    {
        Assert.False(WaterThresholdEvaluator.Evaluate(14m, 100m, new WaterThresholds(null, null, null, null)).Exceeded);
    }
}
