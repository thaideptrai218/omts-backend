using OMTS.Application.Features.Reports.EndOfDay;

namespace OMTS.Application.UnitTests.Features.Reports;

/// <summary>The period of the end-of-day report: Vietnam local time to the minute, both ends included, at most 366 days.</summary>
public class EndOfDayPeriodTests
{
    private static readonly DateOnly Today = new(2026, 11, 19);

    [Fact]
    public void No_period_is_today_from_midnight_to_the_last_minute()
    {
        var period = EndOfDayPeriod.Resolve(null, null, null, Today).Value!;
        Assert.Equal(new DateTime(2026, 11, 19, 0, 0, 0), period.From);
        Assert.Equal(new DateTime(2026, 11, 19, 23, 59, 0), period.To);
        Assert.False(period.SpansDays);
    }

    [Fact]
    public void The_older_date_parameter_is_the_whole_of_that_day()
    {
        var period = EndOfDayPeriod.Resolve(new DateOnly(2026, 11, 3), null, null, Today).Value!;
        Assert.Equal("2026-11-03T00:00", period.FromText);
        Assert.Equal("2026-11-03T23:59", period.ToText);
    }

    [Fact]
    public void From_and_to_win_over_the_date_and_the_end_minute_is_included()
    {
        var period = EndOfDayPeriod.Resolve(new DateOnly(2026, 1, 1), "2026-11-18T08:30", "2026-11-19T17:45", Today).Value!;
        Assert.Equal("2026-11-18T08:30", period.FromText);
        Assert.Equal("2026-11-19T17:45", period.ToText);
        Assert.True(period.SpansDays);

        // 17:45 is in, 17:46 is not: the first instant after the period is the start of the minute that follows.
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 17, 46, 0, TimeSpan.FromHours(7)), period.ToExclusive);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 8, 30, 0, TimeSpan.FromHours(7)), period.FromInstant);
    }

    [Fact]
    public void Only_a_start_runs_to_the_end_of_its_day_and_only_an_end_starts_at_midnight_of_its_day()
    {
        var fromOnly = EndOfDayPeriod.Resolve(null, "2026-11-10T09:00", null, Today).Value!;
        Assert.Equal("2026-11-10T23:59", fromOnly.ToText);
        var toOnly = EndOfDayPeriod.Resolve(null, null, "2026-11-12T10:00", Today).Value!;
        Assert.Equal("2026-11-12T00:00", toOnly.FromText);
    }

    [Fact]
    public void Seconds_are_accepted_and_dropped()
    {
        var period = EndOfDayPeriod.Resolve(null, "2026-11-10T09:00:30", "2026-11-10T10:00:59", Today).Value!;
        Assert.Equal("2026-11-10T10:00", period.ToText);
    }

    [Theory]
    [InlineData("2026-11-19T10:00", "2026-11-19T09:59")]
    [InlineData("2026-11-19T10:00", "2026-11-18T23:59")]
    public void An_end_before_the_start_is_invalid_and_names_to(string from, string to)
    {
        var result = EndOfDayPeriod.Resolve(null, from, to, Today);
        Assert.False(result.IsSuccess);
        Assert.Equal("to", result.Error!.Details!["field"]);
    }

    [Fact]
    public void A_period_of_366_days_is_the_longest()
    {
        Assert.True(EndOfDayPeriod.Resolve(null, "2026-01-01T00:00", "2027-01-02T00:00", Today).IsSuccess);
        var tooLong = EndOfDayPeriod.Resolve(null, "2026-01-01T00:00", "2027-01-02T00:01", Today);
        Assert.False(tooLong.IsSuccess);
        Assert.Equal("to", tooLong.Error!.Details!["field"]);
    }

    [Theory]
    [InlineData("2026-11-19", null, "from")]
    [InlineData("19/11/2026 10:00", null, "from")]
    [InlineData(null, "2026-11-19T25:00", "to")]
    [InlineData(null, "tomorrow", "to")]
    public void A_value_that_is_not_yyyy_MM_ddTHH_mm_is_invalid_and_names_its_field(string? from, string? to, string field)
    {
        var result = EndOfDayPeriod.Resolve(null, from, to, Today);
        Assert.False(result.IsSuccess);
        Assert.Equal(field, result.Error!.Details!["field"]);
    }

    [Fact]
    public void The_file_name_has_the_end_day_only_when_the_period_reaches_another_day()
    {
        var one = EndOfDayPeriod.Resolve(null, "2026-11-19T08:00", "2026-11-19T12:00", Today).Value!;
        Assert.Equal("BaoCaoCuoiNgay_BanHang_20261119", EndOfDayDocumentBuilder.FileBaseName(EndOfDayConcern.Sales, one));
        var many = EndOfDayPeriod.Resolve(null, "2026-11-01T00:00", "2026-11-19T12:00", Today).Value!;
        Assert.Equal("BaoCaoCuoiNgay_ThuChi_20261101-20261119", EndOfDayDocumentBuilder.FileBaseName(EndOfDayConcern.Money, many));
        Assert.Equal("BaoCaoCuoiNgay_HangHoa_20261101-20261119", EndOfDayDocumentBuilder.FileBaseName(EndOfDayConcern.Goods, many));
        Assert.Equal("BaoCaoCuoiNgay_TongHop_20261119", EndOfDayDocumentBuilder.FileBaseName(EndOfDayConcern.Summary, one));
    }
}
