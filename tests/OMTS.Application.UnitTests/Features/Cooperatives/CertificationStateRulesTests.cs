using OMTS.Application.Common;
using OMTS.Application.Features.Cooperatives;

namespace OMTS.Application.UnitTests.Features.Cooperatives;

/// <summary>
/// BR-FARM-06 for cooperative certifications: the state is worked out from the expiry date, never stored. BR-ORIGIN-01:
/// the read URL of the file on the provenance page ends before the certification turns Expired.
/// </summary>
public class CertificationStateRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 31);

    [Theory]
    [InlineData(400, CertificationState.Valid)]
    [InlineData(31, CertificationState.Valid)]
    [InlineData(30, CertificationState.ExpiringSoon)]
    [InlineData(1, CertificationState.ExpiringSoon)]
    [InlineData(0, CertificationState.ExpiringSoon)]
    [InlineData(-1, CertificationState.Expired)]
    [InlineData(-365, CertificationState.Expired)]
    public void State_follows_the_30_day_window_and_the_expiry_day(int daysToExpiry, CertificationState expected) =>
        Assert.Equal(expected, CertificationStateRules.StateOn(Today.AddDays(daysToExpiry), Today));

    [Fact]
    public void A_certification_without_an_expiry_date_is_always_valid() =>
        Assert.Equal(CertificationState.Valid, CertificationStateRules.StateOn(null, Today));

    // Across a month end and a leap day the count is in calendar days.
    [Fact]
    public void Window_counts_calendar_days_across_february()
    {
        var day = new DateOnly(2028, 2, 1);

        Assert.Equal(CertificationState.ExpiringSoon, CertificationStateRules.StateOn(new DateOnly(2028, 3, 2), day));
        Assert.Equal(CertificationState.Valid, CertificationStateRules.StateOn(new DateOnly(2028, 3, 3), day));
    }

    // Before its last 10 minutes and on any other day, the expiry day cannot end during a 10-minute URL.
    [Theory]
    [InlineData("12:00:00")]
    [InlineData("23:49:59")]
    public void File_url_of_the_expiry_day_lives_the_full_lifetime_until_its_last_minutes(string time) =>
        Assert.Equal(TimeSpan.FromMinutes(10), CertificationStateRules.PublicFileLifetime(Today, At(Today, time)));

    // The URL ends at 23:59:59 local time whenever it is issued in the last minutes of the expiry day.
    [Theory]
    [InlineData("23:50:00", 599)]
    [InlineData("23:55:00", 299)]
    [InlineData("23:59:58", 1)]
    [InlineData("23:59:59", 0)]
    public void File_url_ends_with_the_last_second_of_the_expiry_day(string time, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CertificationStateRules.PublicFileLifetime(Today, At(Today, time)));

    [Fact]
    public void File_url_of_a_later_expiry_or_of_no_expiry_date_lives_the_full_lifetime()
    {
        var lastMinute = At(Today, "23:59:00");

        Assert.Equal(TimeSpan.FromMinutes(10), CertificationStateRules.PublicFileLifetime(Today.AddDays(1), lastMinute));
        Assert.Equal(TimeSpan.FromMinutes(10), CertificationStateRules.PublicFileLifetime(null, lastMinute));
        // The largest date the API and the column accept must not overflow.
        Assert.Equal(TimeSpan.FromMinutes(10), CertificationStateRules.PublicFileLifetime(DateOnly.MaxValue, lastMinute));
    }

    private static DateTimeOffset At(DateOnly day, string time) => BusinessClock.FromLocal(day.ToDateTime(TimeOnly.Parse(time)));
}
