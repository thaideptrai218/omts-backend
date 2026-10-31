using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>Field rules of ENV-01/ENV-02 (values, time, reason) and ENV-04 (threshold pairs, MSG-WATER-05).</summary>
public class WaterInputRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, true)]
    [InlineData(14, true)]
    [InlineData(7.25, true)]
    [InlineData(14.01, false)]
    [InlineData(-0.01, false)]
    [InlineData(7.255, false)]
    public void Ph_is_0_to_14_with_two_decimals(double value, bool valid) =>
        Assert.Equal(valid, WaterValueRules.IsValidPh((decimal)value));

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(100.01, false)]
    [InlineData(-1, false)]
    public void Salinity_is_0_to_100(double value, bool valid) =>
        Assert.Equal(valid, WaterValueRules.IsValidSalinity((decimal)value));

    [Fact]
    public void Measured_at_may_be_up_to_five_minutes_ahead_of_the_server()
    {
        Assert.True(WaterValueRules.IsValidMeasuredAt(Now.AddMinutes(5), Now));
        Assert.False(WaterValueRules.IsValidMeasuredAt(Now.AddMinutes(5).AddSeconds(1), Now));
    }

    [Fact]
    public void Required_values_are_named_when_missing_and_optional_ones_only_when_invalid()
    {
        Assert.Equal(["pH", "Salinity", "Measured at"], WaterValueRules.InvalidFields(null, null, null, Now, required: true));
        Assert.Empty(WaterValueRules.InvalidFields(null, null, null, Now, required: false));
        Assert.Equal(["Salinity"], WaterValueRules.InvalidFields(7m, 101m, Now, Now, required: false));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  sai số  ", "sai số")]
    public void Reason_is_trimmed_and_required(string? reason, string? expected) =>
        Assert.Equal(expected, WaterValueRules.Reason(reason));

    [Fact]
    public void Reason_longer_than_1000_characters_is_refused()
    {
        Assert.NotNull(WaterValueRules.Reason(new string('a', 1000)));
        Assert.Null(WaterValueRules.Reason(new string('a', 1001)));
    }

    [Fact]
    public void Thresholds_pairs_are_set_or_cleared_together()
    {
        var cleared = WaterThresholdRules.Validate(new SetWaterThresholdsRequest(null, null, null, null));
        var halfPair = WaterThresholdRules.Validate(new SetWaterThresholdsRequest(6.5m, null, null, 30m));

        Assert.True(cleared.IsSuccess);
        Assert.Equal("MSG-GEN-01", halfPair.Error!.Message.Code);
        Assert.Equal(new[] { "pH maximum", "Salinity minimum" }, (IEnumerable<string>)halfPair.Error.Details!["fields"]!);
    }

    [Fact]
    public void Thresholds_out_of_range_are_invalid_before_the_order_check()
    {
        var result = WaterThresholdRules.Validate(new SetWaterThresholdsRequest(9m, 15m, 50m, 10m));

        Assert.Equal("MSG-GEN-01", result.Error!.Message.Code);
        Assert.Equal("pH maximum", result.Error.Details!["field"]);
    }

    [Theory]
    [InlineData(8.5, 8.5, 10, 20, "pH minimum", "pH")]
    [InlineData(6.5, 8.5, 20, 10, "Salinity minimum", "salinity")]
    [InlineData(8.5, 6.5, 20, 10, "pH minimum", "pH and salinity")]
    public void Minimum_not_less_than_maximum_is_MSG_WATER_05(double phMin, double phMax, double salMin, double salMax, string field, string indicator)
    {
        var result = WaterThresholdRules.Validate(new SetWaterThresholdsRequest((decimal)phMin, (decimal)phMax, (decimal)salMin, (decimal)salMax));

        Assert.Equal("MSG-WATER-05", result.Error!.Message.Code);
        Assert.Equal("MSG28", result.Error.Message.LegacyCode);
        Assert.Equal(
            $"The minimum of {indicator} must be less than the maximum. The thresholds were not saved. Please correct the values.",
            result.Error.Message.Text);
        Assert.Equal(field, result.Error.Details!["field"]);
    }
}
