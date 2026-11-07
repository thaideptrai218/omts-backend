using OMTS.Application.Features.WaterQuality;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>GFR-04: the base date, more than one calendar month, and the repeat a month after the last reminder.</summary>
public class WaterOverdueRuleTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    [Fact]
    public void The_base_is_the_later_of_the_latest_measurement_and_the_earliest_open_start()
    {
        var measured = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.FromHours(7));

        Assert.Equal(new DateOnly(2026, 9, 10), WaterOverdueRule.BaseDate(measured, new DateOnly(2026, 9, 1)));
        Assert.Equal(new DateOnly(2026, 9, 20), WaterOverdueRule.BaseDate(measured, new DateOnly(2026, 9, 20)));
        Assert.Equal(new DateOnly(2026, 9, 20), WaterOverdueRule.BaseDate(null, new DateOnly(2026, 9, 20)));
    }

    [Fact]
    public void The_measurement_counts_by_its_vietnam_date()
    {
        // 22/09 18:00 UTC is 23/09 01:00 in Vietnam.
        var measured = new DateTimeOffset(2026, 9, 22, 18, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 9, 23), WaterOverdueRule.BaseDate(measured, new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void Exactly_one_calendar_month_is_not_due_one_day_more_is()
    {
        Assert.False(WaterOverdueRule.IsDue(new DateOnly(2026, 9, 23), null, Today));
        Assert.True(WaterOverdueRule.IsDue(new DateOnly(2026, 9, 22), null, Today));
    }

    [Fact]
    public void Month_end_follows_the_calendar()
    {
        // 31/01 + 1 month = 28/02 (2026 is not a leap year).
        Assert.False(WaterOverdueRule.IsDue(new DateOnly(2026, 1, 31), null, new DateOnly(2026, 2, 28)));
        Assert.True(WaterOverdueRule.IsDue(new DateOnly(2026, 1, 31), null, new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void The_reminder_repeats_a_month_after_the_last_one_while_the_base_stays_overdue()
    {
        var overdueBase = new DateOnly(2026, 6, 1);

        Assert.False(WaterOverdueRule.IsDue(overdueBase, Today, Today));
        Assert.False(WaterOverdueRule.IsDue(overdueBase, new DateOnly(2026, 9, 24), Today));
        Assert.True(WaterOverdueRule.IsDue(overdueBase, new DateOnly(2026, 9, 23), Today));
        Assert.True(WaterOverdueRule.IsDue(overdueBase, new DateOnly(2026, 7, 1), Today));
    }

    [Fact]
    public void A_base_on_time_is_not_due_however_old_the_last_reminder_is()
    {
        Assert.False(WaterOverdueRule.IsDue(new DateOnly(2026, 10, 1), new DateOnly(2026, 1, 1), Today));
    }

    // Reminded on 01/10; a measurement of 25/09 saved afterwards moves the base. Overdue from 26/10, but the month since
    // the reminder is up on 01/11. The re-arm rule that sent it on 26/10 is gone (PM 03/11).
    [Fact]
    public void A_measurement_older_than_the_last_reminder_waits_for_the_month_since_that_reminder()
    {
        var reminded = new DateOnly(2026, 10, 1);
        var measured = new DateOnly(2026, 9, 25);

        Assert.False(WaterOverdueRule.IsDue(measured, reminded, new DateOnly(2026, 10, 26)));
        Assert.True(WaterOverdueRule.IsDue(measured, reminded, new DateOnly(2026, 11, 1)));
    }

    // Reminded on 15/02; the measurement of 20/02 is voided on 01/03, which moves the base back to 01/01.
    [Fact]
    public void A_void_moves_the_base_back_and_the_repeat_stays_on_its_month()
    {
        var movedBack = new DateOnly(2026, 1, 1);
        var reminded = new DateOnly(2026, 2, 15);

        Assert.False(WaterOverdueRule.IsDue(new DateOnly(2026, 2, 20), reminded, new DateOnly(2026, 3, 2)));
        Assert.False(WaterOverdueRule.IsDue(movedBack, reminded, new DateOnly(2026, 3, 2)));
        Assert.True(WaterOverdueRule.IsDue(movedBack, reminded, new DateOnly(2026, 3, 15)));
    }
}
