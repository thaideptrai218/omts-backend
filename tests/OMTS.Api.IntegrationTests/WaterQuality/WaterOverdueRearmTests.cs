using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// OMTS-214: corrections and voids (ENV-02) re-arm the overdue reminder per zone and measured day. A correction that
/// keeps a valid measurement on the same (or an earlier) day never sends a second reminder for a lapse already
/// reminded; one that moves the zone's measurement to another day, or a void of the measurement that answered the
/// reminder, lets it fire again for the period that results. Each test has its own database (the job scans every zone).
/// </summary>
public class WaterOverdueRearmTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [Fact]
    public async Task Same_day_correction_of_the_reminded_measurement_sends_no_second_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var measuredAt = DateTimeOffset.UtcNow.AddDays(-40);
        var reminded = await z.RecordedAsync(7m, 20m, measuredAt: measuredAt);
        await RunAsync(api, Today);

        await CorrectAsync(z, reminded.Id(), measuredAt);
        await RunAsync(api, Today.AddDays(1));
        await RunAsync(api, Today.AddDays(2));

        Assert.Equal(1, await RemindersAsync(api, z));
    }

    [Fact]
    public async Task Correction_to_an_earlier_day_of_a_reminded_measurement_sends_no_second_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var reminded = await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);

        await CorrectAsync(z, reminded.Id(), DateTimeOffset.UtcNow.AddDays(-41));
        await RunAsync(api, Today.AddDays(1));

        Assert.Equal(1, await RemindersAsync(api, z));
    }

    [Fact]
    public async Task Correction_to_a_later_day_starts_a_new_period_that_is_reminded_when_it_lapses()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        // Typed 40 days too early: the zone looks overdue and is reminded.
        var typo = await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);

        await CorrectAsync(z, typo.Id(), DateTimeOffset.UtcNow.AddHours(-1));
        await RunAsync(api, Today.AddDays(1));
        Assert.Equal(1, await RemindersAsync(api, z));

        await RunAsync(api, Today.AddMonths(1).AddDays(2));
        Assert.Equal(2, await RemindersAsync(api, z));
    }

    [Fact]
    public async Task Correction_that_moves_the_answer_to_a_reminder_back_to_an_overdue_day_reminds_again()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);
        var answer = await z.RecordedAsync(7.1m, 20m);
        await RunAsync(api, Today.AddDays(1));
        Assert.Equal(1, await RemindersAsync(api, z));

        // The answer was really measured 35 days ago: the zone has had no valid measurement for that period.
        await CorrectAsync(z, answer.Id(), DateTimeOffset.UtcNow.AddDays(-35));
        await RunAsync(api, Today.AddDays(1));

        Assert.Equal(2, await RemindersAsync(api, z));
    }

    [Fact]
    public async Task Void_of_the_measurement_that_answered_the_reminder_re_arms_it()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);
        var answer = await z.RecordedAsync(7.1m, 20m);
        await RunAsync(api, Today.AddDays(1));

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(answer.Id(), 1)).StatusCode);
        Assert.Null(await MarkerAsync(api, z));
        await RunAsync(api, Today.AddDays(1));
        await RunAsync(api, Today.AddDays(2));

        Assert.Equal(2, await RemindersAsync(api, z));
    }

    [Fact]
    public async Task Void_of_the_reminded_measurement_itself_or_of_an_older_one_keeps_the_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var reminded = await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        // Synced after the reminder but older than the zone's latest measurement: it never started the period.
        await RunAsync(api, Today);
        var olderLateSync = await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-45));
        var marker = await MarkerAsync(api, z);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(olderLateSync.Id(), 1)).StatusCode);
        Assert.Equal(marker, await MarkerAsync(api, z));
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(reminded.Id(), 1)).StatusCode);
        Assert.Equal(marker, await MarkerAsync(api, z));
        await RunAsync(api, Today.AddDays(1));

        Assert.NotNull(marker);
        Assert.Equal(1, await RemindersAsync(api, z));
    }

    private static async Task<JsonElement> CorrectAsync(WaterZone z, Guid id, DateTimeOffset measuredAt)
    {
        var response = await z.Owner.CorrectAsync(id, 7.4m, 21m, recordVersion: 1, measuredAt: measuredAt);
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

    /// <summary>Overdue reminders the zone's Owner received.</summary>
    private static Task<int> RemindersAsync(WaterApiFactory api, WaterZone z) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking().CountAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue
            && n.RelatedRecordCode == z.Zone.Code
            && n.RecipientAccountId == z.OwnerAccount.Id));
}
