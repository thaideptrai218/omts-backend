using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA (OMTS-214 item 4, date-dependent tests): the overdue job at month ends, with pinned dates. "One calendar
/// month" after the last day of a short month is the same day number of the next month (30/09 → 30/10), so
/// <c>today.AddMonths(-1)</c> is NOT "exactly one month ago" on the 29th–31st: 31/10 minus one month is 30/09,
/// which is already overdue on 31/10. A test that builds its on-time measurement that way fails on such days.
/// </summary>
public class WaterOverdueMonthEndQaTests
{
    [Theory]
    [InlineData("2026-09-30", "2026-10-30", "2026-10-31")]
    [InlineData("2026-01-31", "2026-02-28", "2026-03-01")]
    [InlineData("2026-02-28", "2026-03-28", "2026-03-29")]
    public async Task A_measurement_is_on_time_until_the_same_day_of_the_next_month_and_overdue_the_day_after(
        string measured, string lastOnTimeDay, string firstOverdueDay)
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(DateOnly.Parse(measured).AddMonths(-3));
        await z.AddMeasurementAsync(DateOnly.Parse(measured));

        var onTime = await api.RemindOnAsync(DateOnly.Parse(lastOnTimeDay));
        var overdue = await api.RemindOnAsync(DateOnly.Parse(firstOverdueDay));

        Assert.Equal(0, onTime);
        Assert.Equal(1, overdue);
        Assert.NotEmpty(await api.RemindersOfAsync(z.Zone));
    }

    // Reminded on 31/01: the month is up on 28/02, then 28/03 (the repeat counts from the reminder before it).
    [Fact]
    public async Task The_repeat_follows_the_calendar_at_a_month_end()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(new DateOnly(2025, 12, 1));

        Assert.Equal(1, await api.RemindOnAsync(new DateOnly(2026, 1, 31)));
        Assert.Equal(0, await api.RemindOnAsync(new DateOnly(2026, 2, 27)));
        Assert.Equal(1, await api.RemindOnAsync(new DateOnly(2026, 2, 28)));
        Assert.Equal(0, await api.RemindOnAsync(new DateOnly(2026, 3, 27)));
        Assert.Equal(1, await api.RemindOnAsync(new DateOnly(2026, 3, 28)));
    }
}
