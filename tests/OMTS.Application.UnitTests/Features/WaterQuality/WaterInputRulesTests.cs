using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>Field rules of WATER-01/WATER-02 (values, time, reason) and WATER-04 (threshold pairs, MSG-WATER-05).</summary>
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
    [InlineData(50, true)]
    [InlineData(50.01, false)]
    [InlineData(-1, false)]
    public void Salinity_is_0_to_50(double value, bool valid) =>
        Assert.Equal(valid, WaterValueRules.IsValidSalinity((decimal)value));

    [Theory]
    [InlineData(0, true)]
    [InlineData(45, true)]
    [InlineData(28.5, true)]
    [InlineData(45.01, false)]
    [InlineData(-0.01, false)]
    [InlineData(28.555, false)]
    public void Temperature_is_0_to_45_with_two_decimals(double value, bool valid) =>
        Assert.Equal(valid, WaterValueRules.IsValidTemperature((decimal)value));

    [Fact]
    public void Measured_at_may_be_up_to_five_minutes_ahead_of_the_server()
    {
        Assert.True(WaterValueRules.IsValidMeasuredAt(Now.AddMinutes(5), Now));
        Assert.False(WaterValueRules.IsValidMeasuredAt(Now.AddMinutes(5).AddSeconds(1), Now));
    }

    [Fact]
    public void Measured_at_starts_on_the_first_business_day_the_history_can_list()
    {
        var firstInstant = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(7));

        Assert.True(WaterValueRules.IsValidMeasuredAt(firstInstant, Now));
        Assert.True(WaterValueRules.IsValidMeasuredAt(firstInstant.ToUniversalTime(), Now));
        Assert.False(WaterValueRules.IsValidMeasuredAt(firstInstant.AddTicks(-1), Now));
        Assert.False(WaterValueRules.IsValidMeasuredAt(DateTimeOffset.MinValue, Now));
    }

    [Fact]
    public void Every_value_is_required_and_the_missing_or_invalid_ones_are_named()
    {
        Assert.Equal(["pH", "Salinity", "Temperature", "Measured at"], WaterValueRules.InvalidFields(null, null, null, null, Now));
        Assert.Equal(["Salinity", "Temperature"], WaterValueRules.InvalidFields(7m, 51m, 46m, Now, Now));
        Assert.Empty(WaterValueRules.InvalidFields(7m, 50m, 45m, Now, Now));
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
        var halfPair = WaterThresholdRules.Validate(new SetWaterThresholdsRequest(6.5m, null, null, 30m, null, 35m));

        Assert.True(cleared.IsSuccess);
        Assert.Equal("MSG-GEN-01", halfPair.Error!.Message.Code);
        Assert.Equal(new[] { "pH maximum", "Salinity minimum", "Temperature minimum" }, (IEnumerable<string>)halfPair.Error.Details!["fields"]!);
    }

    [Fact]
    public void Thresholds_keep_the_temperature_pair()
    {
        var result = WaterThresholdRules.Validate(new SetWaterThresholdsRequest(6.5m, 8.5m, 15m, 30m, 10m, 35m));

        Assert.Equal(new WaterThresholds(6.5m, 8.5m, 15m, 30m, 10m, 35m), result.Value);
    }

    [Theory]
    [InlineData(9, 15, 40, 10, 5, 6, "pH maximum")]
    [InlineData(9, 10, 40, 51, 5, 6, "Salinity maximum")]
    [InlineData(9, 10, 40, 45, 5, 46, "Temperature maximum")]
    public void Thresholds_out_of_range_are_invalid_before_the_order_check(double phMin, double phMax, double salMin, double salMax, double tempMin, double tempMax, string field)
    {
        var result = WaterThresholdRules.Validate(new SetWaterThresholdsRequest((decimal)phMin, (decimal)phMax, (decimal)salMin, (decimal)salMax, (decimal)tempMin, (decimal)tempMax));

        Assert.Equal("MSG-GEN-01", result.Error!.Message.Code);
        Assert.Equal(field, result.Error.Details!["field"]);
    }

    [Theory]
    [InlineData(8.5, 8.5, 10, 20, 10, 35, "pH minimum", "pH")]
    [InlineData(6.5, 8.5, 20, 10, 10, 35, "Salinity minimum", "salinity")]
    [InlineData(6.5, 8.5, 10, 20, 35, 35, "Temperature minimum", "temperature")]
    [InlineData(8.5, 6.5, 20, 10, 10, 35, "pH minimum", "pH and salinity")]
    [InlineData(8.5, 6.5, 20, 10, 35, 10, "pH minimum", "pH, salinity and temperature")]
    public void Minimum_not_less_than_maximum_is_MSG_WATER_05(double phMin, double phMax, double salMin, double salMax, double tempMin, double tempMax, string field, string indicator)
    {
        var result = WaterThresholdRules.Validate(new SetWaterThresholdsRequest((decimal)phMin, (decimal)phMax, (decimal)salMin, (decimal)salMax, (decimal)tempMin, (decimal)tempMax));

        Assert.Equal("MSG-WATER-05", result.Error!.Message.Code);
        Assert.Equal("MSG28", result.Error.Message.LegacyCode);
        Assert.Equal(
            $"The minimum of {indicator} must be less than the maximum. The thresholds were not saved. Please correct the values.",
            result.Error.Message.Text);
        Assert.Equal(field, result.Error.Details!["field"]);
    }
}
