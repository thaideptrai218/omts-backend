using OMTS.Application.Common.Messages;

namespace OMTS.Application.UnitTests.Common.Messages;

/// <summary>The Vietnamese text of a water value outside its thresholds: names, units, direction wording and number format.</summary>
public class WaterValueOutsideTests
{
    [Theory]
    [InlineData("PH", 14, null, WaterValueDirection.High, 6.5, 8.5, "pH 14 cao hơn mức cho phép 8,5")]
    [InlineData("PH", 5.9, null, WaterValueDirection.Low, 6.5, 8.5, "pH 5,9 thấp hơn mức cho phép 6,5")]
    [InlineData("SALINITY", 10, "‰", WaterValueDirection.Low, 18, 32, "Độ mặn 10 ‰ thấp hơn mức cho phép 18 ‰")]
    [InlineData("SALINITY", 31.25, "‰", WaterValueDirection.High, 18, 30, "Độ mặn 31,25 ‰ cao hơn mức cho phép 30 ‰")]
    [InlineData("TEMPERATURE", 40, "°C", WaterValueDirection.High, 15, 32, "Nhiệt độ 40 °C cao hơn mức cho phép 32 °C")]
    [InlineData("TEMPERATURE", 9.5, "°C", WaterValueDirection.Low, 10, 35, "Nhiệt độ 9,5 °C thấp hơn mức cho phép 10 °C")]
    public void Describe_names_the_value_and_the_limit_it_crossed(
        string indicator, double value, string? unit, WaterValueDirection direction, double min, double max, string expected)
    {
        var outside = new WaterValueOutside(indicator, (decimal)value, unit, direction, (decimal)min, (decimal)max);

        Assert.Equal(expected, outside.Describe());
    }

    [Fact]
    public void Numbers_have_a_decimal_comma_and_no_trailing_zeros()
    {
        var outside = new WaterValueOutside(WaterValueOutside.Ph, 14.00m, null, WaterValueDirection.High, 6.50m, 8.50m);

        Assert.Equal("pH 14 cao hơn mức cho phép 8,5", outside.Describe());
    }

    [Fact]
    public void Numbers_do_not_depend_on_the_culture_of_the_server()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var outside = new WaterValueOutside(WaterValueOutside.Temperature, 1234.5m, "°C", WaterValueDirection.High, 10m, 35.25m);

            Assert.Equal("Nhiệt độ 1234,5 °C cao hơn mức cho phép 35,25 °C", outside.Describe());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Only_the_crossed_limit_is_shown_and_a_limit_not_set_is_left_out()
    {
        var onlyMax = new WaterValueOutside(WaterValueOutside.Ph, 9m, null, WaterValueDirection.High, null, 8.5m);
        var noLimit = new WaterValueOutside(WaterValueOutside.Ph, 9m, null, WaterValueDirection.High, 6.5m, null);

        Assert.Equal("pH 9 cao hơn mức cho phép 8,5", onlyMax.Describe());
        Assert.Equal("pH 9 cao hơn mức cho phép", noLimit.Describe());
    }

    [Fact]
    public void Brief_text_for_an_sms_drops_the_words_before_the_limit()
    {
        var outside = new WaterValueOutside(WaterValueOutside.Salinity, 12m, "‰", WaterValueDirection.Low, 15m, 30m);

        Assert.Equal("Độ mặn 12 ‰ thấp hơn 15 ‰", outside.DescribeBrief());
    }

    [Fact]
    public void An_unknown_indicator_keeps_its_code()
    {
        var outside = new WaterValueOutside("TURBIDITY", 5m, null, WaterValueDirection.High, null, 3m);

        Assert.Equal("TURBIDITY 5 cao hơn mức cho phép 3", outside.Describe());
    }
}
