using System.Net;
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
/// Overdue water measurement reminder through the real API flows (QA red team): a voided latest measurement no longer
/// counts, and a correction moving the time counts from its new time.
/// </summary>
public class WaterOverdueRedTeamTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [Fact]
    public async Task Void_and_correction_recorded_through_the_api_move_the_overdue_check()
    {
        await using var api = new WaterApiFactory();
        var voidedLater = await api.ZoneAsync();
        var corrected = await api.ZoneAsync();
        var notMeasuredAgain = await api.ZoneAsync();
        var fortyDaysAgo = DateTimeOffset.UtcNow.AddDays(-40);

        await voidedLater.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        var recent = await voidedLater.RecordedAsync(7.1m, 20m);
        var old = await corrected.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        // The correction fixes the measurement time too: the record is now recent.
        Assert.Equal(HttpStatusCode.OK, (await corrected.Owner.CorrectAsync(old.Id(), 7m, 20m)).StatusCode);
        await notMeasuredAgain.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        // A recognition saves no measurement, so it does not end the overdue period.
        Assert.Equal(HttpStatusCode.OK, (await notMeasuredAgain.RecognizeAsync("ph7.2_sal20")).StatusCode);

        await RunAsync(api, Today);

        Assert.Empty(await RemindersAsync(api, voidedLater.Zone));
        Assert.Empty(await RemindersAsync(api, corrected.Zone));
        Assert.Single(await RemindersAsync(api, notMeasuredAgain.Zone), n => n.RecipientAccountId == notMeasuredAgain.OwnerAccount.Id);

        Assert.Equal(HttpStatusCode.OK, (await voidedLater.Owner.VoidAsync(recent.Id())).StatusCode);
        await RunAsync(api, Today);

        Assert.Single(await RemindersAsync(api, voidedLater.Zone), n => n.RecipientAccountId == voidedLater.OwnerAccount.Id);
        Assert.Empty(await RemindersAsync(api, corrected.Zone));
        Assert.Single(await RemindersAsync(api, notMeasuredAgain.Zone), n => n.RecipientAccountId == notMeasuredAgain.OwnerAccount.Id);
    }

    private static async Task RunAsync(WaterApiFactory api, DateOnly today)
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, CancellationToken.None);
    }

    private static Task<List<Notification>> RemindersAsync(WaterApiFactory api, FarmingZone zone) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordCode == zone.Code)
            .ToListAsync());
}
