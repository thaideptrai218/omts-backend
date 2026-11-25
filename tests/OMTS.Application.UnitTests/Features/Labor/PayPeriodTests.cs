using OMTS.Application.Features.Labor;

namespace OMTS.Application.UnitTests.Features.Labor;

public class PayPeriodTests
{
    [Theory]
    [InlineData(2026, 10, 25, "10/2026")]
    [InlineData(2026, 12, 25, "12/2026")]
    [InlineData(2027, 1, 31, "01/2027")]
    public void A_pay_period_is_named_after_the_month_in_which_it_ends(int year, int month, int day, string name) =>
        Assert.Equal(name, PayPeriod.NameOf(new DateOnly(year, month, day)));
}
