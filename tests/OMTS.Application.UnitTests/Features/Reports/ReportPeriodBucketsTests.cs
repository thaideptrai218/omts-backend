using OMTS.Application.Features.Reports;

namespace OMTS.Application.UnitTests.Features.Reports;

/// <summary>The day, week and month buckets of the period reports: ISO weeks starting on Monday, on business dates (UTC+7).</summary>
public class ReportPeriodBucketsTests
{
    // 2026-11-01 is a Sunday, 2026-11-02 the Monday after it.
    [Theory]
    [InlineData("2026-10-31", "2026-10-26")]
    [InlineData("2026-11-01", "2026-10-26")]
    [InlineData("2026-11-02", "2026-11-02")]
    [InlineData("2026-11-08", "2026-11-02")]
    [InlineData("2026-11-09", "2026-11-09")]
    public void A_week_starts_on_its_monday(string date, string monday) =>
        Assert.Equal(DateOnly.Parse(monday), ReportPeriodBuckets.StartOf(DateOnly.Parse(date), ReportPeriodUnit.Week));

    [Fact]
    public void A_day_is_its_own_bucket_and_a_month_starts_on_the_first()
    {
        var date = new DateOnly(2026, 11, 17);
        Assert.Equal(date, ReportPeriodBuckets.StartOf(date, ReportPeriodUnit.Day));
        Assert.Equal(new DateOnly(2026, 11, 1), ReportPeriodBuckets.StartOf(date, ReportPeriodUnit.Month));
        Assert.Equal(new DateOnly(2026, 2, 1), ReportPeriodBuckets.StartOf(new DateOnly(2026, 2, 28), ReportPeriodUnit.Month));
    }

    [Fact]
    public void The_key_is_the_first_day_and_the_label_is_the_day_the_iso_week_or_the_month()
    {
        var monday = new DateOnly(2026, 11, 2);
        Assert.Equal("2026-11-02", ReportPeriodBuckets.Key(monday));
        Assert.Equal("2026-11-02", ReportPeriodBuckets.Label(monday, ReportPeriodUnit.Day));
        Assert.Equal("2026-W45", ReportPeriodBuckets.Label(monday, ReportPeriodUnit.Week));
        Assert.Equal("2026-11", ReportPeriodBuckets.Label(new DateOnly(2026, 11, 1), ReportPeriodUnit.Month));
    }

    // The first days of January can belong to the last ISO week of the year before: its label names the ISO year, not the calendar year.
    [Fact]
    public void A_week_across_new_year_is_labelled_with_its_iso_year()
    {
        var start = ReportPeriodBuckets.StartOf(new DateOnly(2027, 1, 1), ReportPeriodUnit.Week);

        Assert.Equal(new DateOnly(2026, 12, 28), start);
        Assert.Equal("2026-W53", ReportPeriodBuckets.Label(start, ReportPeriodUnit.Week));
    }

    [Theory]
    [InlineData("day", ReportPeriodUnit.Day)]
    [InlineData(" Week ", ReportPeriodUnit.Week)]
    [InlineData("MONTH", ReportPeriodUnit.Month)]
    public void The_unit_is_read_by_name_in_any_letter_case(string value, ReportPeriodUnit unit) => Assert.Equal(unit, ReportPeriodBuckets.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("year")]
    [InlineData("1")]
    public void Anything_else_is_not_a_unit(string? value) => Assert.Null(ReportPeriodBuckets.Parse(value));
}
