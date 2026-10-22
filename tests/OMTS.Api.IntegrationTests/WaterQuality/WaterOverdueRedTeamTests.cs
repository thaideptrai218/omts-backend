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
/// Overdue water measurement reminder through the real API flows (QA red team): a voided latest measurement
/// no longer counts, a correction version does, and a Draft never ends the overdue period.
/// </summary>
public class WaterOverdueRedTeamTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [Fact]
    public async Task Void_correction_and_draft_recorded_through_the_api_move_the_overdue_check_as_the_ticket_says()
    {
        await using var api = new WaterApiFactory();
        var voidedLater = await api.ZoneAsync();
        var corrected = await api.ZoneAsync();
        var draftOnly = await api.ZoneAsync();
        var fortyDaysAgo = DateTimeOffset.UtcNow.AddDays(-40);

        await voidedLater.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        var recent = await voidedLater.RecordedAsync(7.1m, 20m);
        var old = await corrected.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        // The correction fixes the measurement time too: the new version is the zone's latest confirmed one.
        Assert.Equal(HttpStatusCode.Created, (await corrected.Owner.CorrectAsync(old.Id(), 7m, 20m, recordVersion: 1)).StatusCode);
        await draftOnly.RecordedAsync(7m, 20m, measuredAt: fortyDaysAgo);
        await draftOnly.RecordedAsync(7.2m, 20m, confirm: false);

        await RunAsync(api, Today);

        Assert.Empty(await RemindersAsync(api, voidedLater.Zone));
        Assert.Empty(await RemindersAsync(api, corrected.Zone));
        Assert.Single(await RemindersAsync(api, draftOnly.Zone), n => n.RecipientAccountId == draftOnly.OwnerAccount.Id);

        Assert.Equal(HttpStatusCode.OK, (await voidedLater.Owner.VoidAsync(recent.Id(), 1)).StatusCode);
        await RunAsync(api, Today);

        Assert.Single(await RemindersAsync(api, voidedLater.Zone), n => n.RecipientAccountId == voidedLater.OwnerAccount.Id);
        Assert.Empty(await RemindersAsync(api, corrected.Zone));
        Assert.Single(await RemindersAsync(api, draftOnly.Zone), n => n.RecipientAccountId == draftOnly.OwnerAccount.Id);
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
