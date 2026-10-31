using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of OMTS-214 item 1 (corrections and voids re-arm the overdue reminder per zone and measured day): correction
/// chains, corrections that change which measurement is the zone's latest, voids of later-day corrections and of
/// drafts, late syncs, and runs on the same day. Each test has its own in-memory database (the job scans every zone).
/// The reminder marker is the real "now" of each run, so the order of runs, corrections and voids below is the order
/// of their times.
/// </summary>
public class WaterOverdueRearmQaTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    // BUG (path dependence): 08-20 reminded → corrected to 08-10 → corrected again to 08-15. Each step moves the
    // measurement to a day before the reminded one, and a single correction 08-20 → 08-15 sends nothing, but
    // PeriodKnownAt compares each version only with the one it corrects, so the second fix counts as "a later day"
    // and a second MSG26 is sent for a lapse already reminded, with no new measurement.
    [Fact]
    public async Task Correcting_a_correction_back_to_a_day_still_before_the_reminded_one_sends_no_second_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var reminded = await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);
        Assert.Equal(1, await RemindersAsync(api, z));

        var earlier = await CorrectAsync(z, reminded.Id(), recordVersion: 1, DateTimeOffset.UtcNow.AddDays(-50));
        await RunAsync(api, Today.AddDays(1));
        Assert.Equal(1, await RemindersAsync(api, z));

        await CorrectAsync(z, earlier.Id(), recordVersion: 2, DateTimeOffset.UtcNow.AddDays(-45));
        await RunAsync(api, Today.AddDays(2));

        Assert.Equal(1, await RemindersAsync(api, z));
    }

    // BUG (missed reminder): the answer to the reminder is corrected to a day before the reminded measurement. The
    // zone is left with no valid measurement since the reminded one, exactly as after a void of the answer (which
    // re-arms) or a correction of the answer to a day after the reminded one (which reminds again, see
    // WaterOverdueRearmTests). Here the latest is the reminded measurement again, known before the marker: no reminder.
    [Fact]
    public async Task Correcting_the_answer_to_a_day_before_the_reminded_measurement_re_arms_like_a_void()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);
        var answer = await z.RecordedAsync(7.1m, 20m);
        await RunAsync(api, Today.AddDays(1));
        Assert.Equal(1, await RemindersAsync(api, z));

        // The "answer" was an old paper reading typed with today's date: really taken 45 days ago.
        await CorrectAsync(z, answer.Id(), recordVersion: 1, DateTimeOffset.UtcNow.AddDays(-45));
        await RunAsync(api, Today.AddDays(1));

        Assert.Equal(2, await RemindersAsync(api, z));
    }

    // Same-day fixes of a same-day fix are one measurement: never a second reminder.
    [Fact]
    public async Task Chain_of_same_day_corrections_never_sends_a_second_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        // 08:00 Vietnam time, so the correction one hour later stays on the same business day at any time of the run.
        var day = OMTS.Application.Common.BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow).AddDays(-40);
        var measuredAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(7));
        var first = await z.RecordedAsync(7m, 20m, measuredAt: measuredAt);
        await RunAsync(api, Today);

        var second = await CorrectAsync(z, first.Id(), recordVersion: 1, measuredAt.AddHours(1));
        await RunAsync(api, Today.AddDays(1));
        var third = await CorrectAsync(z, second.Id(), recordVersion: 2, measuredAt.AddMinutes(10));
        await RunAsync(api, Today.AddDays(2));
        await CorrectAsync(z, third.Id(), recordVersion: 3, measuredAt);
        await RunAsync(api, Today.AddDays(3));

        Assert.Equal(1, await RemindersAsync(api, z));
    }

    // A correction to a later day counts as a newer measurement; voiding it again leaves the zone with its older
    // measurement only, a lapse the reminder already covered once but that the void re-opens (spec: the voided
    // latest became known after the reminder).
    [Fact]
    public async Task Void_of_a_later_day_correction_re_arms_the_lapse_of_the_older_measurement()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-50));
        var typo = await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);
        Assert.Equal(1, await RemindersAsync(api, z));

        var corrected = await CorrectAsync(z, typo.Id(), recordVersion: 1, DateTimeOffset.UtcNow.AddHours(-1));
        await RunAsync(api, Today.AddDays(1));
        Assert.Equal(1, await RemindersAsync(api, z));

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(corrected.Id(), 2)).StatusCode);
        Assert.Null(await MarkerAsync(api, z));
        await RunAsync(api, Today.AddDays(1));
        await RunAsync(api, Today.AddDays(1));

        Assert.Equal(2, await RemindersAsync(api, z));
    }

    // Voiding a same-day correction made after the reminder: the measurement it fixed was known before the reminder,
    // so the void keeps the marker (the lapse was already reminded).
    [Fact]
    public async Task Void_of_a_same_day_correction_of_the_reminded_measurement_keeps_the_marker()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-60));
        var measuredAt = DateTimeOffset.UtcNow.AddDays(-40);
        var reminded = await z.RecordedAsync(7m, 20m, measuredAt: measuredAt);
        await RunAsync(api, Today);
        var marker = await MarkerAsync(api, z);

        var fixedValue = await CorrectAsync(z, reminded.Id(), recordVersion: 1, measuredAt);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(fixedValue.Id(), 2)).StatusCode);
        await RunAsync(api, Today.AddDays(1));

        Assert.NotNull(marker);
        Assert.Equal(marker, await MarkerAsync(api, z));
        Assert.Equal(1, await RemindersAsync(api, z));
    }

    // A Draft never counts: voiding one changes nothing.
    [Fact]
    public async Task Void_of_a_draft_keeps_the_marker()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);
        var marker = await MarkerAsync(api, z);
        var draft = await z.RecordedAsync(7.1m, 20m, confirm: false);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(draft.Id(), 1)).StatusCode);
        await RunAsync(api, Today.AddDays(1));

        Assert.NotNull(marker);
        Assert.Equal(marker, await MarkerAsync(api, z));
        Assert.Equal(1, await RemindersAsync(api, z));
    }

    // Late sync (unchanged rule): a newer measurement confirmed after the reminder starts a new period even when it
    // is itself overdue; an older one synced late does not.
    [Fact]
    public async Task Late_synced_newer_measurement_reminds_again_and_a_late_synced_older_one_does_not()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-45));
        await RunAsync(api, Today);

        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today.AddDays(1));
        Assert.Equal(2, await RemindersAsync(api, z));

        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-50));
        await RunAsync(api, Today.AddDays(2));
        Assert.Equal(2, await RemindersAsync(api, z));
    }

    // After a re-arming void, a fresh measurement before the next run answers it: nothing is sent; two runs on the
    // same day after a re-arm send one reminder.
    [Fact]
    public async Task Re_armed_zone_is_reminded_once_per_day_and_not_at_all_when_measured_again_in_time()
    {
        await using var api = new WaterApiFactory();
        var answered = await api.ZoneAsync();
        var overdue = await api.ZoneAsync();
        foreach (var z in new[] { answered, overdue })
        {
            await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        }

        await RunAsync(api, Today);
        var answers = new Dictionary<WaterZone, JsonElement>();
        foreach (var z in new[] { answered, overdue })
        {
            answers[z] = await z.RecordedAsync(7.1m, 20m);
            Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(answers[z].Id(), 1)).StatusCode);
        }

        await answered.RecordedAsync(7.2m, 20m);
        await RunAsync(api, Today.AddDays(1));
        await RunAsync(api, Today.AddDays(1));

        Assert.Equal(1, await RemindersAsync(api, answered));
        Assert.Equal(2, await RemindersAsync(api, overdue));
    }

    private static async Task<JsonElement> CorrectAsync(WaterZone z, Guid id, int recordVersion, DateTimeOffset measuredAt)
    {
        var response = await z.Owner.CorrectAsync(id, 7.4m, 21m, recordVersion, measuredAt: measuredAt);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task RunAsync(WaterApiFactory api, DateOnly today)
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, CancellationToken.None);
    }

    private static Task<DateTimeOffset?> MarkerAsync(WaterApiFactory api, WaterZone z) =>
        api.QueryAsync(db => db.FarmingZones.AsNoTracking().Where(x => x.Id == z.Zone.Id).Select(x => x.WaterOverdueRemindedAt).SingleAsync());

    private static Task<int> RemindersAsync(WaterApiFactory api, WaterZone z) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking().CountAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue
            && n.RelatedRecordCode == z.Zone.Code
            && n.RecipientAccountId == z.OwnerAccount.Id));
}
