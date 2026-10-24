using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// Overdue reminder with offline sync (QA red team): a measurement taken before the reminder but synced after
/// it is still newer than the one that started the overdue period, so it ends that period; when the zone
/// lapses again it must be reminded again ("remind again only after a newer confirmed measurement has ended
/// the previous period and the zone becomes overdue again").
/// </summary>
public class WaterOverdueBackdatedSyncTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [Fact]
    public async Task Measurement_synced_after_the_reminder_but_taken_before_it_still_re_arms_the_next_reminder()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        await RunAsync(api, Today);
        Assert.Single(await RemindersAsync(api, z));

        // Measured offline five days ago, synced only now (after the reminder).
        await z.RecordedAsync(7.1m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-5));
        await RunAsync(api, Today.AddDays(1));
        Assert.Single(await RemindersAsync(api, z));

        // A month after that measurement the zone is overdue again: a new period, a new reminder.
        await RunAsync(api, Today.AddMonths(1));

        Assert.Equal(2, (await RemindersAsync(api, z)).Count);
    }

    private static async Task RunAsync(WaterApiFactory api, DateOnly today)
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, CancellationToken.None);
    }

    private static Task<List<Notification>> RemindersAsync(WaterApiFactory api, WaterZone z) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WaterMeasurementOverdue
                && n.RelatedRecordCode == z.Zone.Code
                && n.RecipientAccountId == z.OwnerAccount.Id)
            .ToListAsync());
}
