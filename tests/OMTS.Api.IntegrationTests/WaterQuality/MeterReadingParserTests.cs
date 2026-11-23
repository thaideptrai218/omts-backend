using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Ocr.Paddle;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01 step 6 with the PaddleOCR sidecar: turning the lines read on a meter display into suggestions. The fixtures are
/// what the sidecar read (640 px, PP-OCRv5 mobile) on photos of real meters from Wikimedia Commons, with their score and box.
/// </summary>
public class MeterReadingParserTests
{
    private static readonly MeterProfile NoLayout = new(2, 1, 1, []);
    private static readonly MeterProfile PhThenTemperature = new(2, 1, 1, [MeterIndicator.Ph, MeterIndicator.Temperature]);

    [Fact]
    public void A_value_next_to_its_label_is_read_and_a_lost_decimal_point_is_put_back_as_a_guess()
    {
        // Hanna Checker, display crop: "10.33" read as "1033", the pH label left of it on its own line.
        var (ph, salinity, temperature) = MeterReadingParser.Parse([Line("1033", 0.9989, 185, 27, 521, 184), Line("pH", 0.997, 33, 113, 111, 174)], NoLayout);

        Assert.Equal(10.33m, ph!.Value);
        Assert.Equal(MeterReadingParser.GuessedConfidence, ph.Confidence);
        Assert.Null(salinity);
        Assert.Null(temperature);
    }

    [Fact]
    public void Labels_on_the_same_line_give_every_indicator_with_the_recogniser_score()
    {
        var (ph, salinity, temperature) = MeterReadingParser.Parse(
            [Line("pH 7.82", 0.97, 0, 0, 200, 40), Line("25.5 ppt", 0.96, 0, 50, 200, 90), Line("28.6°C", 0.95, 0, 100, 200, 140)], NoLayout);

        Assert.Equal((7.82m, 0.97), (ph!.Value, ph.Confidence));
        Assert.Equal((25.5m, 0.96), (salinity!.Value, salinity.Confidence));
        Assert.Equal((28.6m, 0.95), (temperature!.Value, temperature.Confidence));
    }

    [Fact]
    public void A_C_right_after_the_number_is_a_temperature_and_a_value_with_no_label_stays_unread_without_a_layout()
    {
        // WTW inoLab, display crop: "5.739" has no readable label, "23.c" is 23 °C.
        var lines = new[] { Line("5.739", 0.9941, 59, 24, 243, 110), Line("23.c", 0.7717, 141, 89, 245, 153) };

        var (ph, _, temperature) = MeterReadingParser.Parse(lines, NoLayout);

        Assert.Null(ph);
        // The meter shows one decimal: "23" without one may have lost a digit, so it is flagged.
        Assert.Equal((23m, MeterReadingParser.GuessedConfidence), (temperature!.Value, temperature.Confidence));
    }

    [Fact]
    public void Two_labelled_values_on_one_line_each_keep_their_own_label()
    {
        // The sidecar often merges a display row into one line; "Sal" names 25.1, not 7.82.
        var (ph, salinity, temperature) = MeterReadingParser.Parse([Line("pH 7.82 Sal 25.1", 0.95, 0, 0, 300, 40), Line("28.6°C 3.69 pH", 0.9, 0, 50, 300, 90)], NoLayout);

        Assert.Equal((7.82m, 0.95), (ph!.Value, ph.Confidence));
        Assert.Equal((25.1m, 0.95), (salinity!.Value, salinity.Confidence));
        Assert.Equal((28.6m, 0.9), (temperature!.Value, temperature.Confidence));
    }

    [Theory]
    [InlineData("pH ７.８２")] // full-width digits
    [InlineData("pH ٧٫٨٢")] // Arabic-Indic digits
    public void Digits_that_are_not_ASCII_are_ignored_without_an_exception(string text)
    {
        var (ph, _, _) = MeterReadingParser.Parse([Line(text, 0.9, 0, 0, 200, 40)], NoLayout);

        Assert.Null(ph);
    }

    [Fact]
    public void A_clock_or_a_date_on_the_display_does_not_shift_the_layout()
    {
        var (ph, _, temperature) = MeterReadingParser.Parse(
            [Line("10:25", 0.9, 0, 0, 100, 30), Line("24/11", 0.9, 120, 0, 220, 30), Line("7.82", 0.95, 0, 40, 200, 90), Line("28.6", 0.95, 0, 100, 200, 150)], PhThenTemperature);

        Assert.Equal(7.82m, ph!.Value);
        Assert.Equal(28.6m, temperature!.Value);
    }

    [Fact]
    public void When_the_unlabelled_values_do_not_match_the_layout_slots_none_is_assigned()
    {
        // An unlabelled conductivity value above pH and temperature: pairing by position would shift every value.
        var (ph, _, temperature) = MeterReadingParser.Parse(
            [Line("1413", 0.9, 0, 0, 200, 40), Line("7.82", 0.95, 0, 50, 200, 90), Line("28.6", 0.95, 0, 100, 200, 140)], PhThenTemperature);

        Assert.Null(ph);
        Assert.Null(temperature);
    }

    [Theory]
    [InlineData("5 to 40 °C")]
    [InlineData("5–40°C")]
    [InlineData("0~50°C")]
    [InlineData("pH 0.00-14.00")]
    [InlineData("pH 0.00~14.00")]
    public void Printed_ranges_and_specifications_are_not_readings(string text)
    {
        var (ph, _, temperature) = MeterReadingParser.Parse([Line(text, 0.95, 0, 0, 300, 40)], NoLayout);

        Assert.Null(ph);
        Assert.Null(temperature);
    }

    [Fact]
    public void A_value_with_fewer_decimals_than_the_meter_shows_or_outside_the_usual_range_is_flagged()
    {
        var (ph, salinity, _) = MeterReadingParser.Parse([Line("pH 7", 0.95, 0, 0, 100, 40), Line("Sal 2.5", 0.95, 0, 50, 100, 90)], NoLayout);

        Assert.Equal((7m, MeterReadingParser.GuessedConfidence), (ph!.Value, ph.Confidence));
        Assert.Equal((2.5m, MeterReadingParser.GuessedConfidence), (salinity!.Value, salinity.Confidence)); // below the usual 5‰
    }

    [Fact]
    public void Conductivity_with_a_Greek_mu_is_not_an_indicator()
    {
        var (ph, salinity, temperature) = MeterReadingParser.Parse([Line("1413 μS", 0.95, 0, 0, 200, 40)], new MeterProfile(2, 1, 1, [MeterIndicator.Salinity]));

        Assert.Null(ph);
        Assert.Null(salinity);
        Assert.Null(temperature);
    }

    [Fact]
    public void The_meter_layout_names_unlabelled_values_in_reading_order_as_guesses_rounded_to_two_decimals()
    {
        var wtw = MeterReadingParser.Parse([Line("5.739", 0.9941, 59, 24, 243, 110), Line("23.c", 0.7717, 141, 89, 245, 153)], PhThenTemperature);
        // Hanna HI pH meter, display crop: pH and temperature on one line, the temperature's point lost.
        var hanna = MeterReadingParser.Parse([Line("3.69\" 252", 0.9054, 300, 537, 416, 586)], PhThenTemperature);

        Assert.Equal((5.74m, MeterReadingParser.GuessedConfidence), (wtw.Ph!.Value, wtw.Ph.Confidence));
        Assert.Equal(23m, wtw.Temperature!.Value); // labelled, so the layout slot is not used
        Assert.Null(hanna.Ph); // pH 3.69 is a lab sample: outside the usual 5–10 of farm water, a layout guess is refused
        Assert.Equal((25.2m, MeterReadingParser.GuessedConfidence), (hanna.Temperature!.Value, hanna.Temperature.Confidence));
        Assert.Null(hanna.Salinity);
    }

    [Theory]
    [InlineData("000")] // a TDS pen
    [InlineData("120")] // an infrared thermometer at 120.3 °C, point lost
    public void A_layout_guess_outside_the_usual_farm_water_range_is_not_suggested(string text)
    {
        var (ph, _, temperature) = MeterReadingParser.Parse([Line(text, 0.97, 43, 77, 385, 304)], PhThenTemperature);

        Assert.Null(ph);
        Assert.Null(temperature);
    }

    [Fact]
    public void A_layout_slot_already_filled_by_a_label_leaves_the_unlabelled_value_to_the_next_slot()
    {
        // pH carries its label; the only unlabelled value is the temperature, second in the layout.
        var (ph, _, temperature) = MeterReadingParser.Parse([Line("pH 8.12", 0.95, 0, 0, 200, 40), Line("27.4", 0.93, 0, 60, 200, 100)], PhThenTemperature);

        Assert.Equal(8.12m, ph!.Value);
        Assert.Equal((27.4m, MeterReadingParser.GuessedConfidence), (temperature!.Value, temperature.Confidence));
    }

    [Fact]
    public void Unlabelled_values_are_never_guessed_from_their_range()
    {
        // 8.3 could be pH or salinity, 25.2 salinity or temperature: without a layout nothing is suggested.
        var (ph, salinity, temperature) = MeterReadingParser.Parse([Line("83", 0.9973, 183, 83, 358, 224), Line("3.69\" 252", 0.9, 0, 300, 200, 340)], NoLayout);

        Assert.Null(ph);
        Assert.Null(salinity);
        Assert.Null(temperature);
    }

    [Fact]
    public void Conductivity_model_codes_printed_ranges_and_impossible_values_are_ignored()
    {
        // Martini Mi 170 (µS + °C, 73.4 °C is outside the valid range), WTW model "pH7110", a thermometer's printed range.
        var lines = new[]
        {
            Line(".US", 0.6028, 166, 32, 214, 67), Line("412", 0.9934, 195, 35, 477, 194), Line("734°", 0.9104, 281, 226, 480, 338),
            Line("pH7110", 0.9977, 203, 512, 237, 526), Line("-20C t0+70", 0.849, 351, 650, 451, 665), Line("Mi 170 Bench Meter", 0.95, 111, 729, 176, 740),
        };

        var (ph, salinity, temperature) = MeterReadingParser.Parse(lines, PhThenTemperature);

        Assert.Null(ph);
        Assert.Null(salinity);
        Assert.Null(temperature);
    }

    [Fact]
    public void Of_two_readings_of_the_same_indicator_the_better_score_wins()
    {
        var (ph, _, _) = MeterReadingParser.Parse([Line("pH 7.10", 0.6, 0, 0, 100, 30), Line("pH 7.16", 0.9, 0, 200, 100, 230)], NoLayout);

        Assert.Equal(7.16m, ph!.Value);
    }

    [Theory]
    [InlineData("Ph,Oxygen")]
    [InlineData("Temperature,7")]
    public void An_unknown_indicator_in_the_layout_is_a_configuration_error(string layout)
    {
        var options = new PaddleOcrOptions { Layout = layout };

        Assert.Throws<InvalidOperationException>(() => options.Profile());
    }

    [Fact]
    public void The_layout_setting_is_read_case_insensitively_with_spaces()
    {
        Assert.Equal([MeterIndicator.Ph, MeterIndicator.Temperature], new PaddleOcrOptions { Layout = " ph , TEMPERATURE " }.Profile().Layout);
        Assert.Empty(new PaddleOcrOptions().Profile().Layout);
    }

    private static OcrLine Line(string text, double score, int left, int top, int right, int bottom) => new(text, score, left, top, right, bottom);
}
