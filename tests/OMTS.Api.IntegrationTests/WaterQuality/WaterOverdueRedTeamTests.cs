using System.Net;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Domain.Entities.Platform;
using static OMTS.Api.IntegrationTests.WaterQuality.WaterOverdueTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>GFR-04 through the real API flows: the monthly repeat, and what a new measurement, a correction and a void do to the base.</summary>
public class WaterOverdueRedTeamTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [Fact]
    public async Task The_reminder_is_sent_again_every_month_while_the_zone_has_no_new_measurement()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var start = new DateOnly(2026, 1, 10);
        await z.OpenCycleAsync(start);
        var first = new DateOnly(2026, 2, 11);

        Assert.Equal(0, await api.RemindOnAsync(first.AddDays(-1)));
        Assert.Equal(1, await api.RemindOnAsync(first));
        // A second run on the day and the days before the month is up send nothing.
        Assert.Equal(0, await api.RemindOnAsync(first));
        Assert.Equal(0, await api.RemindOnAsync(new DateOnly(2026, 3, 10)));
        Assert.Equal(1, await api.RemindOnAsync(new DateOnly(2026, 3, 11)));
        Assert.Equal(0, await api.RemindOnAsync(new DateOnly(2026, 4, 10)));
        Assert.Equal(1, await api.RemindOnAsync(new DateOnly(2026, 4, 11)));

        var reminders = await api.RemindersOfAsync(z.Zone);
        Assert.Equal(3, reminders.Count(n => n.RecipientAccountId == manager.Id));
        Assert.All(reminders, n => Assert.Contains($"since {Named(start)}.", n.Body));
    }

    // A missed month (the host was down) is one reminder, and the next is a month after it.
    [Fact]
    public async Task A_run_after_a_gap_sends_one_reminder_and_the_next_waits_a_month_from_it()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(new DateOnly(2026, 1, 10));

        Assert.Equal(1, await api.RemindOnAsync(new DateOnly(2026, 5, 20)));
        Assert.Equal(0, await api.RemindOnAsync(new DateOnly(2026, 6, 19)));
        Assert.Equal(1, await api.RemindOnAsync(new DateOnly(2026, 6, 20)));
    }

    [Fact]
    public async Task A_new_measurement_ends_the_repeat_until_a_month_after_it()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(new DateOnly(2026, 1, 10));
        Assert.Equal(1, await api.RemindOnAsync(new DateOnly(2026, 2, 20)));

        var measured = new DateOnly(2026, 2, 25);
        await z.AddMeasurementAsync(measured);

        Assert.Equal(0, await api.RemindOnAsync(new DateOnly(2026, 3, 20)));
        Assert.Equal(0, await api.RemindOnAsync(measured.AddMonths(1)));
        Assert.Equal(1, await api.RemindOnAsync(measured.AddMonths(1).AddDays(1)));
        var last = (await api.RemindersOfAsync(z.Zone)).OrderBy(n => n.CreatedAt).Last();
        Assert.Contains($"since {Named(measured)}.", last.Body);
    }

    [Fact]
    public async Task A_void_moves_the_base_back_and_a_correction_moves_it_to_its_new_time()
    {
        await using var api = new WaterApiFactory();
        var voidedLater = await api.ZoneAsync();
        var corrected = await api.ZoneAsync();
        var notMeasuredAgain = await api.ZoneAsync();
        var fortyDaysAgo = DateTimeOffset.UtcNow.AddDays(-40);
        foreach (var z in new[] { voidedLater, corrected, notMeasuredAgain })
        {
            await z.OpenCycleAsync(Today.AddMonths(-6));
        }

        await voidedLater.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        var recent = await voidedLater.RecordedAsync(7.1m, 20m);
        var old = await corrected.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        Assert.Equal(HttpStatusCode.OK, (await corrected.Owner.CorrectAsync(old.Id(), 7m, 20m)).StatusCode);
        await notMeasuredAgain.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        // A recognition saves no measurement, so it moves nothing.
        Assert.Equal(HttpStatusCode.OK, (await notMeasuredAgain.RecognizeAsync("ph7.2_sal20")).StatusCode);

        Assert.Equal(1, await api.RemindOnAsync(Today));
        Assert.Empty(await api.RemindersOfAsync(voidedLater.Zone));
        Assert.Empty(await api.RemindersOfAsync(corrected.Zone));
        Assert.NotEmpty(await api.RemindersOfAsync(notMeasuredAgain.Zone));

        Assert.Equal(HttpStatusCode.OK, (await voidedLater.Owner.VoidAsync(recent.Id())).StatusCode);
        Assert.Equal(1, await api.RemindOnAsync(Today));

        var fortyDaysAgoLocal = BusinessClock.ToBusinessDate(fortyDaysAgo);
        Assert.All(await api.RemindersOfAsync(voidedLater.Zone), n => Assert.Contains($"since {Named(fortyDaysAgoLocal)}.", n.Body));
        Assert.Empty(await api.RemindersOfAsync(corrected.Zone));
    }

    // PM 03/11: no re-arm. A void moves the base, the repeat still counts a month from the last reminder.
    [Fact]
    public async Task A_void_after_a_reminder_sends_nothing_before_the_month_since_that_reminder_is_up()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.OpenCycleAsync(Today.AddMonths(-6));
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        Assert.Equal(1, await api.RemindOnAsync(Today));
        var reminders = (await api.RemindersOfAsync(z.Zone)).Count;
        var answer = await z.RecordedAsync(7.1m, 20m);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(answer.Id())).StatusCode);

        Assert.Equal(0, await api.RemindOnAsync(Today.AddDays(1)));
        Assert.Equal(0, await api.RemindOnAsync(Today.AddMonths(1).AddDays(-1)));
        Assert.Equal(1, await api.RemindOnAsync(Today.AddMonths(1)));
        Assert.Equal(reminders * 2, (await api.RemindersOfAsync(z.Zone)).Count);
    }
}
