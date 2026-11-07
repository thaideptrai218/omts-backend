using OMTS.Application.Common.Messages;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>BR-WATER-04: below min or above max is outside; equal to a bound is not; an empty bound or a value not read is not checked.</summary>
public class WaterThresholdEvaluatorTests
{
    private static readonly WaterThresholds Full = new(6.5m, 8.5m, 15m, 30m, 10m, 35m);

    [Theory]
    [InlineData(6.5, 15, 10)]
    [InlineData(8.5, 30, 35)]
    [InlineData(7.2, 22, 25)]
    public void Values_within_or_equal_to_the_bounds_raise_no_alert(double ph, double salinity, double temperature)
    {
        var result = WaterThresholdEvaluator.Evaluate((decimal)ph, (decimal)salinity, (decimal)temperature, Full);

        Assert.False(result.Exceeded);
        Assert.Empty(result.ViolatedMetrics);
        Assert.Null(result.ViolatedValues);
        Assert.Empty(result.ValuesOutside);
    }

    [Fact]
    public void Each_violation_is_named_with_value_and_bound()
    {
        var result = WaterThresholdEvaluator.Evaluate(9.1m, 12m, 36m, Full);

        Assert.True(result.Exceeded);
        Assert.Equal(["pH", "Salinity", "Temperature"], result.ViolatedMetrics);
        Assert.Equal("pH 9.10 > 8.50; Salinity 12.00 < 15.00; Temperature 36.00 > 35.00", result.ViolatedValues);
        Assert.Equal(
            ["pH 9.1 is too high (allowed 6.5–8.5)", "Salinity 12 ‰ is too low (allowed 15–30 ‰)", "Temperature 36 °C is too high (allowed 10–35 °C)"],
            result.ValuesOutside.Select(v => v.Describe()));
        Assert.Equal([WaterValueOutside.Ph, WaterValueOutside.Salinity, WaterValueOutside.Temperature], result.ValuesOutside.Select(v => v.Indicator));
    }

    [Fact]
    public void Value_just_outside_a_bound_is_a_violation()
    {
        Assert.Equal("pH 6.49 < 6.50", WaterThresholdEvaluator.Evaluate(6.49m, 20m, 25m, Full).ViolatedValues);
        Assert.Equal("Salinity 30.01 > 30.00", WaterThresholdEvaluator.Evaluate(7m, 30.01m, 25m, Full).ViolatedValues);
        Assert.Equal("Temperature 9.99 < 10.00", WaterThresholdEvaluator.Evaluate(7m, 20m, 9.99m, Full).ViolatedValues);
    }

    [Fact]
    public void Only_the_bounds_that_are_set_are_checked()
    {
        var onlyPhMax = new WaterThresholds(null, 8.5m, null, null);

        Assert.False(WaterThresholdEvaluator.Evaluate(0m, 50m, 45m, onlyPhMax).Exceeded);
        var high = WaterThresholdEvaluator.Evaluate(8.6m, 0m, 0m, onlyPhMax);
        Assert.Equal(["pH"], high.ViolatedMetrics);
        Assert.Equal("pH 8.60 > 8.50", high.ViolatedValues);
    }

    [Fact]
    public void A_value_not_read_is_not_checked()
    {
        var partial = WaterThresholdEvaluator.Evaluate(null, 12m, null, Full);

        Assert.Equal(["Salinity"], partial.ViolatedMetrics);
        Assert.False(WaterThresholdEvaluator.Evaluate(null, null, null, Full).Exceeded);
    }

    [Fact]
    public void No_thresholds_means_no_alert()
    {
        Assert.False(WaterThresholdEvaluator.Evaluate(14m, 50m, 45m, new WaterThresholds(null, null, null, null)).Exceeded);
    }
}
