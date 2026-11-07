using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.UnitTests.Features.Farming;

public class FarmingRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    [Theory]
    [InlineData(30, LicenseState.ExpiringSoon)]
    [InlineData(31, LicenseState.Valid)]
    [InlineData(0, LicenseState.ExpiringSoon)]
    [InlineData(-1, LicenseState.Expired)]
    public void License_state_boundaries_around_the_expiry_date(int daysLeft, LicenseState expected) =>
        Assert.Equal(expected, LicenseStateRules.StateOn(new LicenseFacts(Today.AddYears(-1), Today.AddDays(daysLeft), null), Today));

    [Fact]
    public void License_without_expiry_is_valid_and_one_starting_later_is_not_yet_effective()
    {
        Assert.Equal(LicenseState.Valid, LicenseStateRules.StateOn(new LicenseFacts(Today, null, null), Today));
        Assert.Equal(LicenseState.NotYetEffective, LicenseStateRules.StateOn(new LicenseFacts(Today.AddDays(1), null, null), Today));
        Assert.Equal(LicenseState.Valid, LicenseStateRules.StateOn(new LicenseFacts(Today, Today.AddDays(31), null), Today));
    }

    // BR-FARM-06: Replaced from the day the replacing license is recorded, whatever the dates, and before every other state.
    [Fact]
    public void Replaced_from_the_record_day_of_the_replacement_whatever_the_dates()
    {
        var expired = new LicenseFacts(Today.AddYears(-2), Today.AddDays(-10), ReplacedOn: Today);

        Assert.Equal(LicenseState.Expired, LicenseStateRules.StateOn(expired, Today.AddDays(-1)));
        Assert.Equal(LicenseState.Replaced, LicenseStateRules.StateOn(expired, Today));
        Assert.Equal(LicenseState.Replaced, LicenseStateRules.StateOn(expired, Today.AddYears(5)));
        // A license replaced before it was even effective, and one replaced while Valid or Expiring soon.
        Assert.Equal(LicenseState.Replaced, LicenseStateRules.StateOn(new LicenseFacts(Today.AddDays(30), Today.AddYears(3), Today), Today));
        Assert.Equal(LicenseState.NotYetEffective, LicenseStateRules.StateOn(new LicenseFacts(Today.AddDays(30), Today.AddYears(3), Today), Today.AddDays(-1)));
        Assert.Equal(LicenseState.Replaced, LicenseStateRules.StateOn(new LicenseFacts(Today.AddYears(-1), Today.AddDays(5), Today), Today));
        Assert.Equal(LicenseState.ExpiringSoon, LicenseStateRules.StateOn(new LicenseFacts(Today.AddYears(-1), Today.AddDays(5), Today), Today.AddDays(-1)));
    }
}
