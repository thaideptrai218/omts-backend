using OMTS.Application.Features.Cooperatives;

namespace OMTS.Application.UnitTests.Features.Cooperatives;

/// <summary>BR-FARM-06 for cooperative certifications: the state is worked out from the expiry date, never stored.</summary>
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
}
