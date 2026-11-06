using OMTS.Application.Features.Labor;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>
/// QA of OMTS-227, BR-LABOR-13 against an oracle that shares nothing with the code: it walks the calendar one day at a time and
/// never adds a month. Literal rows are worked out by hand from the rule. The range holds two leap years and every month end.
/// </summary>
public sealed class PayPeriodCalendarQaTests
{
    private static readonly DateOnly RangeStart = new(2023, 12, 1);
    private static readonly DateOnly RangeEnd = new(2029, 3, 31);

    private static readonly int[] EveryStartDay = Enumerable.Range(1, 28).ToArray();

    private static DateOnly D(string date) => DateOnly.Parse(date);

    private static IEnumerable<DateOnly> EveryDay(DateOnly from, DateOnly to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    /// <summary>The latest day on or before the date whose day of the month is the start day.</summary>
    private static DateOnly OracleStart(DateOnly day, int startDay)
    {
        while (day.Day != startDay)
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    /// <summary>The day before the first day after <paramref name="start"/> whose day of the month is the start day.</summary>
    private static DateOnly OracleEnd(DateOnly start, int startDay)
    {
        var next = start.AddDays(1);
        while (next.Day != startDay)
        {
            next = next.AddDays(1);
        }

        return next.AddDays(-1);
    }

    [Fact]
    public void Every_start_day_and_every_date_fall_in_the_period_the_oracle_walks_to()
    {
        foreach (var startDay in EveryStartDay)
        {
            foreach (var day in EveryDay(RangeStart, RangeEnd))
            {
                var start = OracleStart(day, startDay);
                var expected = new PayPeriod(start, OracleEnd(start, startDay));

                Assert.Equal(expected, PayPeriodCalendar.Aligned(day, startDay));
                Assert.Equal(expected, PayPeriodCalendar.Containing(day, startDay, []));
            }
        }
    }

    [Fact]
    public void After_any_last_day_the_next_period_is_the_one_the_oracle_walks_to()
    {
        foreach (var startDay in EveryStartDay)
        {
            foreach (var previousEnd in EveryDay(RangeStart, RangeEnd))
            {
                var start = previousEnd.AddDays(1);

                var period = PayPeriodCalendar.After(previousEnd, startDay);

                Assert.Equal(new PayPeriod(start, OracleEnd(start, startDay)), period);
                Assert.InRange(period.End.DayNumber - period.Start.DayNumber + 1, 1, 31);
            }
        }
    }

    // A pay period is a month that starts on the start day: every day in exactly one period, and each period has its own name.
    [Fact]
    public void With_one_start_day_the_periods_cover_every_day_once_and_no_two_share_a_name()
    {
        foreach (var startDay in EveryStartDay)
        {
            var period = PayPeriodCalendar.Aligned(RangeStart, startDay);
            var names = new HashSet<string>();
            var expectedStart = period.Start;
            while (period.Start <= RangeEnd)
            {
                Assert.Equal(expectedStart, period.Start);
                Assert.Equal(startDay, period.Start.Day);
                Assert.True(names.Add(period.Name), $"Start day {startDay}: two periods named {period.Name}.");
                Assert.Equal($"{period.End.Month:00}/{period.End.Year}", period.Name);
                expectedStart = period.End.AddDays(1);
                period = PayPeriodCalendar.After(period.End, startDay);
            }
        }
    }

    [Theory]
    [InlineData("2026-03-31", 28, "2026-03-28", "2026-04-27", "04/2026")] // day 31, start day 28
    [InlineData("2026-03-29", 28, "2026-03-28", "2026-04-27", "04/2026")] // day 29, in a month that has it
    [InlineData("2026-02-28", 28, "2026-02-28", "2026-03-27", "03/2026")] // the last day of February is the start day
    [InlineData("2026-02-27", 28, "2026-01-28", "2026-02-27", "02/2026")]
    [InlineData("2028-02-29", 28, "2028-02-28", "2028-03-27", "03/2028")] // the leap day falls after the start day
    [InlineData("2028-02-29", 1, "2028-02-01", "2028-02-29", "02/2028")]
    [InlineData("2027-02-28", 1, "2027-02-01", "2027-02-28", "02/2027")]
    [InlineData("2100-02-28", 1, "2100-02-01", "2100-02-28", "02/2100")] // a century that is no leap year
    [InlineData("2026-12-31", 1, "2026-12-01", "2026-12-31", "12/2026")]
    [InlineData("2027-01-15", 28, "2026-12-28", "2027-01-27", "01/2027")] // across a year, named by its end
    [InlineData("2026-12-28", 28, "2026-12-28", "2027-01-27", "01/2027")]
    [InlineData("2026-12-27", 28, "2026-11-28", "2026-12-27", "12/2026")]
    [InlineData("2026-04-30", 28, "2026-04-28", "2026-05-27", "05/2026")] // 30-day month
    [InlineData("2026-10-10", 26, "2026-09-26", "2026-10-25", "10/2026")] // the example of BR-LABOR-13
    public void Month_ends_and_leap_days_by_hand(string day, int startDay, string start, string end, string name)
    {
        var period = PayPeriodCalendar.Containing(D(day), startDay, []);

        Assert.Equal((D(start), D(end), name), (period.Start, period.End, period.Name));
        Assert.True(period.Contains(D(day)));
    }

    [Theory]
    [InlineData("2026-10-31", 26, "2026-11-01", "2026-11-25", "11/2026")] // the example of BR-LABOR-13
    [InlineData("2026-11-25", 26, "2026-11-26", "2026-12-25", "12/2026")]
    [InlineData("2026-10-31", 2, "2026-11-01", "2026-11-01", "11/2026")] // one day, shorter than the SRS example
    [InlineData("2026-10-31", 1, "2026-11-01", "2026-11-30", "11/2026")]
    [InlineData("2026-10-25", 26, "2026-10-26", "2026-11-25", "11/2026")] // a whole month
    [InlineData("2026-10-25", 27, "2026-10-26", "2026-10-26", "10/2026")] // one day, named as the period before it
    [InlineData("2026-12-31", 15, "2027-01-01", "2027-01-14", "01/2027")] // into the next year
    [InlineData("2027-01-31", 28, "2027-02-01", "2027-02-27", "02/2027")] // short February
    [InlineData("2028-01-31", 28, "2028-02-01", "2028-02-27", "02/2028")]
    [InlineData("2027-02-28", 28, "2027-03-01", "2027-03-27", "03/2027")]
    [InlineData("2028-02-29", 28, "2028-03-01", "2028-03-27", "03/2028")]
    [InlineData("2028-02-29", 1, "2028-03-01", "2028-03-31", "03/2028")]
    [InlineData("2026-03-15", 10, "2026-03-16", "2026-04-09", "04/2026")] // the new day is earlier in the month: not shorter by much
    public void A_change_of_start_day_gives_the_period_of_the_rule(string previousEnd, int startDay, string start, string end, string name)
    {
        var period = PayPeriodCalendar.After(D(previousEnd), startDay);

        Assert.Equal((D(start), D(end), name), (period.Start, period.End, period.Name));
    }

    // PM call, not a defect: "named after the month in which it ends" gives both periods the name 10/2026. See the questions of the QA report.
    [Theory]
    [InlineData("2026-10-25", 1, "2026-10-26", "2026-10-31")] // back to the first of the month
    [InlineData("2026-10-25", 28, "2026-10-26", "2026-10-27")]
    public void A_change_of_start_day_can_give_two_periods_of_one_name_as_the_rule_reads(string previousEnd, int startDay, string start, string end)
    {
        var before = new PayPeriod(D("2026-09-26"), D(previousEnd));

        var after = PayPeriodCalendar.After(before.End, startDay);

        Assert.Equal((D(start), D(end)), (after.Start, after.End));
        Assert.Equal("10/2026", before.Name);
        Assert.Equal(before.Name, after.Name);
        Assert.NotEqual(before.Describe(), after.Describe());
    }

    [Theory]
    [InlineData("2026-10-28", 1, "2026-10-26", "2026-10-31")] // after a period of the old start day
    [InlineData("2026-11-01", 1, "2026-11-01", "2026-11-30")]
    [InlineData("2026-10-26", 28, "2026-10-26", "2026-10-27")]
    [InlineData("2026-10-27", 28, "2026-10-26", "2026-10-27")]
    [InlineData("2026-10-28", 28, "2026-10-28", "2026-11-27")]
    [InlineData("2026-10-25", 1, "2026-09-26", "2026-10-25")] // the stored one, under any start day
    [InlineData("2026-09-26", 1, "2026-09-26", "2026-10-25")]
    [InlineData("2026-09-25", 26, "2026-08-26", "2026-09-25")] // before every stored one
    [InlineData("2026-09-25", 1, "2026-09-01", "2026-09-25")] // clipped by the stored one that follows
    public void A_day_around_a_stored_period_is_in_the_period_of_the_rule(string day, int startDay, string start, string end)
    {
        var stored = new PayPeriod(D("2026-09-26"), D("2026-10-25"));

        Assert.Equal(new PayPeriod(D(start), D(end)), PayPeriodCalendar.Containing(D(day), startDay, [stored]));
    }

    [Theory]
    [InlineData("2026-11-25", "2026-11-01", "2026-11-25")]
    [InlineData("2026-11-26", "2026-11-26", "2026-11-30")] // clipped by December
    [InlineData("2026-11-30", "2026-11-26", "2026-11-30")]
    [InlineData("2026-12-01", "2026-12-01", "2026-12-31")]
    [InlineData("2027-01-01", "2027-01-01", "2027-01-25")] // walks on from the last stored one
    [InlineData("2027-01-26", "2027-01-26", "2027-02-25")]
    public void A_gap_between_stored_periods_is_cut_by_the_new_start_day_and_by_the_next_stored_one(string day, string start, string end)
    {
        PayPeriod[] stored = [new(D("2026-12-01"), D("2026-12-31")), new(D("2026-10-01"), D("2026-10-31"))];

        Assert.Equal(new PayPeriod(D(start), D(end)), PayPeriodCalendar.Containing(D(day), 26, stored));
    }

    [Fact]
    public void Periods_found_for_each_day_around_a_change_join_up_and_never_overlap()
    {
        PayPeriod[] stored = [new(D("2026-09-26"), D("2026-10-25")), new(D("2026-10-26"), D("2026-10-26")), new(D("2027-03-05"), D("2027-04-04"))];
        foreach (var startDay in EveryStartDay)
        {
            PayPeriod? previous = null;
            foreach (var day in EveryDay(D("2026-08-01"), D("2027-08-31")))
            {
                var period = PayPeriodCalendar.Containing(day, startDay, stored);

                Assert.True(period.Contains(day), $"{day} is not in {period} under {startDay}.");
                Assert.True(period.Start <= period.End);
                if (previous is not null && period != previous)
                {
                    Assert.Equal(previous.End.AddDays(1), period.Start);
                }

                previous = period;
            }
        }
    }

    [Fact]
    public void The_next_two_periods_join_up_for_every_start_day()
    {
        foreach (var startDay in EveryStartDay)
        {
            foreach (var today in new[] { D("2026-02-28"), D("2028-02-29"), D("2026-12-31"), D("2027-01-01") })
            {
                var noPayroll = PayPeriodCalendar.NextTwo(null, today, startDay);
                var withPayroll = PayPeriodCalendar.NextTwo(today, today, startDay);

                Assert.True(noPayroll[0].Contains(today));
                Assert.Equal(noPayroll[0].End.AddDays(1), noPayroll[1].Start);
                Assert.Equal(today.AddDays(1), withPayroll[0].Start);
                Assert.Equal(withPayroll[0].End.AddDays(1), withPayroll[1].Start);
                Assert.Equal(startDay, withPayroll[1].Start.Day);
            }
        }
    }
}
