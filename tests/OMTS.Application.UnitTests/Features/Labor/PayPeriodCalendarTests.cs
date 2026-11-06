using OMTS.Application.Features.Labor;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>BR-LABOR-13 on fixed dates: the examples of the SRS, month ends, leap years and periods crossing a month or a year.</summary>
public sealed class PayPeriodCalendarTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static PayPeriod P(DateOnly start, DateOnly end) => new(start, end);

    [Fact]
    public void The_srs_example_of_a_period_is_named_after_the_month_it_ends_in()
    {
        var period = PayPeriodCalendar.Aligned(D(2026, 10, 10), 26);

        Assert.Equal(P(D(2026, 9, 26), D(2026, 10, 25)), period);
        Assert.Equal("10/2026", period.Name);
        Assert.Equal("10/2026 (26/09/2026 to 25/10/2026)", period.Describe());
    }

    [Fact]
    public void The_srs_example_of_a_new_start_day_is_shorter_once()
    {
        var first = PayPeriodCalendar.After(D(2026, 10, 31), 26);
        var second = PayPeriodCalendar.After(first.End, 26);

        Assert.Equal(P(D(2026, 11, 1), D(2026, 11, 25)), first);
        Assert.Equal(P(D(2026, 11, 26), D(2026, 12, 25)), second);
        Assert.Equal(("11/2026", "12/2026"), (first.Name, second.Name));
    }

    [Theory]
    [InlineData(2026, 1, 31, 2026, 2, 1, 2026, 2, 28)] // to the end of a short month
    [InlineData(2028, 1, 31, 2028, 2, 1, 2028, 2, 29)] // a leap year
    [InlineData(2100, 1, 31, 2100, 2, 1, 2100, 2, 28)] // a century that is no leap year
    [InlineData(2026, 4, 30, 2026, 5, 1, 2026, 5, 31)]
    [InlineData(2026, 12, 31, 2027, 1, 1, 2027, 1, 31)] // across a year
    public void A_period_from_the_first_ends_on_the_last_day_of_the_month(int y, int m, int d, int sy, int sm, int sd, int ey, int em, int ed)
    {
        Assert.Equal(P(D(sy, sm, sd), D(ey, em, ed)), PayPeriodCalendar.After(D(y, m, d), 1));
    }

    [Theory]
    [InlineData(2026, 1, 27, 2026, 1, 28, 2026, 2, 27)] // start day 28 crossing February
    [InlineData(2026, 2, 27, 2026, 2, 28, 2026, 3, 27)]
    [InlineData(2028, 2, 27, 2028, 2, 28, 2028, 3, 27)] // 29/02 of a leap year falls inside
    [InlineData(2026, 12, 27, 2026, 12, 28, 2027, 1, 27)] // across a year
    public void A_period_of_the_highest_start_day_crosses_a_month_at_its_last_valid_day(int y, int m, int d, int sy, int sm, int sd, int ey, int em, int ed)
    {
        Assert.Equal(P(D(sy, sm, sd), D(ey, em, ed)), PayPeriodCalendar.After(D(y, m, d), 28));
    }

    [Fact]
    public void A_new_start_day_on_the_very_next_day_makes_a_period_of_one_day()
    {
        // "from that day to the day before the next start day": the SRS sets no shortest period.
        Assert.Equal(P(D(2026, 10, 26), D(2026, 10, 26)), PayPeriodCalendar.After(D(2026, 10, 25), 27));
    }

    [Fact]
    public void A_new_start_day_on_the_first_day_after_the_last_payroll_makes_a_whole_month()
    {
        Assert.Equal(P(D(2026, 10, 26), D(2026, 11, 25)), PayPeriodCalendar.After(D(2026, 10, 25), 26));
    }

    [Theory]
    [InlineData(2026, 10, 26, 2026, 10, 26, 2026, 11, 25)] // on the start day
    [InlineData(2026, 10, 25, 2026, 9, 26, 2026, 10, 25)] // on the last day
    [InlineData(2026, 1, 3, 2025, 12, 26, 2026, 1, 25)] // across a year
    [InlineData(2028, 2, 29, 2028, 2, 26, 2028, 3, 25)] // a leap day
    public void A_day_without_stored_payrolls_falls_in_the_whole_month_of_its_start_day(int y, int m, int d, int sy, int sm, int sd, int ey, int em, int ed)
    {
        Assert.Equal(P(D(sy, sm, sd), D(ey, em, ed)), PayPeriodCalendar.Containing(D(y, m, d), 26, []));
    }

    [Fact]
    public void A_stored_period_keeps_its_days_under_another_start_day()
    {
        var stored = P(D(2026, 10, 1), D(2026, 10, 31));

        Assert.Equal(stored, PayPeriodCalendar.Containing(D(2026, 10, 31), 26, [stored]));
    }

    [Fact]
    public void A_later_day_is_reached_from_the_last_stored_period_by_the_new_start_day()
    {
        var stored = P(D(2026, 10, 1), D(2026, 10, 31));

        Assert.Equal(P(D(2026, 11, 1), D(2026, 11, 25)), PayPeriodCalendar.Containing(D(2026, 11, 25), 26, [stored]));
        Assert.Equal(P(D(2026, 11, 26), D(2026, 12, 25)), PayPeriodCalendar.Containing(D(2026, 11, 26), 26, [stored]));
        Assert.Equal(P(D(2027, 1, 26), D(2027, 2, 25)), PayPeriodCalendar.Containing(D(2027, 2, 1), 26, [stored]));
    }

    [Fact]
    public void A_period_not_stored_yet_ends_before_the_next_stored_one()
    {
        var stored = P(D(2026, 10, 10), D(2026, 11, 9));

        Assert.Equal(P(D(2026, 10, 1), D(2026, 10, 9)), PayPeriodCalendar.Containing(D(2026, 10, 5), 1, [stored]));
    }

    [Fact]
    public void Stored_periods_are_read_in_any_order()
    {
        var (october, december) = (P(D(2026, 10, 1), D(2026, 10, 31)), P(D(2026, 12, 1), D(2026, 12, 31)));

        Assert.Equal(P(D(2026, 11, 1), D(2026, 11, 30)), PayPeriodCalendar.Containing(D(2026, 11, 15), 1, [december, october]));
    }

    [Fact]
    public void The_next_two_periods_follow_the_last_payroll()
    {
        Assert.Equal(
            [P(D(2026, 11, 1), D(2026, 11, 25)), P(D(2026, 11, 26), D(2026, 12, 25))],
            PayPeriodCalendar.NextTwo(D(2026, 10, 31), D(2027, 3, 3), 26));
    }

    [Fact]
    public void The_next_two_periods_without_a_payroll_start_with_the_period_of_today()
    {
        Assert.Equal(
            [P(D(2026, 9, 26), D(2026, 10, 25)), P(D(2026, 10, 26), D(2026, 11, 25))],
            PayPeriodCalendar.NextTwo(null, D(2026, 10, 10), 26));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(28, true)]
    [InlineData(0, false)]
    [InlineData(29, false)]
    [InlineData(31, false)]
    [InlineData(-1, false)]
    public void A_start_day_is_from_1_to_28(int day, bool valid) => Assert.Equal(valid, PayPeriodCalendar.IsStartDay(day));

    [Fact]
    public void No_start_day_is_no_valid_start_day() => Assert.False(PayPeriodCalendar.IsStartDay(null));

    [Fact]
    public void Periods_of_every_start_day_join_up_for_years()
    {
        for (var startDay = PayPeriodCalendar.FirstStartDay; startDay <= PayPeriodCalendar.LastStartDay; startDay++)
        {
            var period = PayPeriodCalendar.Aligned(D(2027, 1, 15), startDay);
            for (var month = 0; month < 60; month++)
            {
                var next = PayPeriodCalendar.After(period.End, startDay);
                Assert.Equal(period.End.AddDays(1), next.Start);
                Assert.Equal(startDay, next.Start.Day);
                Assert.Equal(next.Start.AddMonths(1).AddDays(-1), next.End);
                period = next;
            }
        }
    }
}
