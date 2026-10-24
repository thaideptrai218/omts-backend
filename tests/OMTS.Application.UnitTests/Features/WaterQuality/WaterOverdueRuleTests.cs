using OMTS.Application.Features.WaterQuality;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>Overdue reminder (formerly JOB-05): more than one calendar month, one reminder per overdue period.</summary>
public class WaterOverdueRuleTests
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);
    private static readonly DateOnly Today = new(2026, 10, 23);

    [Fact]
    public void Exactly_one_calendar_month_is_not_overdue_one_day_more_is()
    {
        Assert.False(WaterOverdueRule.IsOverdue(new DateOnly(2026, 9, 23), Today));
        Assert.True(WaterOverdueRule.IsOverdue(new DateOnly(2026, 9, 22), Today));
    }

    [Fact]
    public void Month_end_follows_the_calendar()
    {
        // 31/01 + 1 month = 28/02 (2026 is not a leap year).
        Assert.False(WaterOverdueRule.IsOverdue(new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28)));
        Assert.True(WaterOverdueRule.IsOverdue(new DateOnly(2026, 1, 31), new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void Period_start_uses_the_local_date()
    {
        // 22/09 18:00 UTC is 23/09 01:00 local: still on time on 23/10.
        var measured = new DateTimeOffset(2026, 9, 22, 18, 0, 0, TimeSpan.Zero);

        Assert.False(WaterOverdueRule.NeedsReminder(measured, null, Today));
    }

    [Fact]
    public void One_reminder_per_overdue_period()
    {
        var measured = new DateTimeOffset(2026, 8, 1, 9, 0, 0, Local);
        var remindedInThisPeriod = new DateTimeOffset(2026, 9, 2, 6, 0, 0, Local);
        var remindedInAnEarlierPeriod = measured.AddDays(-1);

        Assert.True(WaterOverdueRule.NeedsReminder(measured, null, Today));
        Assert.False(WaterOverdueRule.NeedsReminder(measured, remindedInThisPeriod, Today));
        Assert.True(WaterOverdueRule.NeedsReminder(measured, remindedInAnEarlierPeriod, Today));
    }
}
