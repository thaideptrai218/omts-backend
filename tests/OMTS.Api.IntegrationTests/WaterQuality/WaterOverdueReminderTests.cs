using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// Overdue water measurement reminder (formerly JOB-05): MSG26 once per overdue period to Owners and the zone's
/// staff with View; only Confirmed measurements count. Each test has its own database (the job scans every zone).
/// </summary>
public class WaterOverdueReminderTests
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [Fact]
    public async Task Overdue_zone_is_reminded_once_per_recipient_and_not_again_while_still_overdue()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var (_, outsider) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        await AddAsync(api, z.Zone, StatusIds.WaterMeasurement.Confirmed, LocalMorning(Today.AddMonths(-2)));

        var first = await RunAsync(api, Today);
        var second = await RunAsync(api, Today.AddDays(1));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        var reminders = await RemindersAsync(api, z.Zone);
        Assert.Single(reminders, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(reminders, n => n.RecipientAccountId == manager.Id);
        Assert.DoesNotContain(reminders, n => n.RecipientAccountId == outsider.Id);
        // MSG-WATER-06 with the date of the latest measurement; a normal type (BR-ACCOUNT-11).
        var lastDate = Today.AddMonths(-2).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(reminders, n =>
        {
            Assert.Equal($"Zone {z.Zone.Name} has had no water measurement since {lastDate}. Please measure the water and record the result in OMTS.", n.Body);
            Assert.Equal(("MSG-WATER-06", false), (n.MessageCode, n.IsMandatory));
            Assert.Equal((z.Zone.Id, z.Zone.Code), (n.RelatedRecordId, n.RelatedRecordCode));
        });
        Assert.NotNull((await api.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(x => x.Id == z.Zone.Id))).WaterOverdueRemindedAt);
    }

    [Fact]
    public async Task One_calendar_month_is_on_time_one_day_more_is_overdue()
    {
        // A pinned mid-month day: one month back is the same day number there (from 31/10 it would be 30/09,
        // already overdue; WaterOverdueMonthEndQaTests covers the month ends).
        var today = new DateOnly(2026, 6, 15);
        await using var api = new WaterApiFactory();
        var onTime = await api.ZoneAsync();
        var late = await api.ZoneAsync();
        await AddAsync(api, onTime.Zone, StatusIds.WaterMeasurement.Confirmed, LocalMorning(today.AddMonths(-1)));
        await AddAsync(api, late.Zone, StatusIds.WaterMeasurement.Confirmed, LocalMorning(today.AddMonths(-1).AddDays(-1)));

        Assert.Equal(1, await RunAsync(api, today));

        Assert.Empty(await RemindersAsync(api, onTime.Zone));
        Assert.NotEmpty(await RemindersAsync(api, late.Zone));
    }

    [Fact]
    public async Task Drafts_corrected_and_voided_measurements_do_not_count()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await AddAsync(api, z.Zone, StatusIds.WaterMeasurement.Confirmed, LocalMorning(Today.AddMonths(-3)));
        foreach (var status in new[] { StatusIds.WaterMeasurement.Draft, StatusIds.WaterMeasurement.Superseded, StatusIds.WaterMeasurement.Voided })
        {
            await AddAsync(api, z.Zone, status, LocalMorning(Today.AddDays(-2)));
        }

        Assert.Equal(1, await RunAsync(api, Today));
    }

    [Fact]
    public async Task Zone_never_measured_counts_from_its_creation_and_discontinued_zones_are_skipped()
    {
        await using var api = new WaterApiFactory();
        var fresh = await api.ZoneAsync();
        var discontinued = await api.ZoneAsync();
        await api.UpdateAsync<FarmingZone>(discontinued.Zone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);

        Assert.Equal(0, await RunAsync(api, Today.AddMonths(1)));
        Assert.Equal(1, await RunAsync(api, Today.AddMonths(1).AddDays(1)));
        Assert.NotEmpty(await RemindersAsync(api, fresh.Zone));
        Assert.Empty(await RemindersAsync(api, discontinued.Zone));
    }

    [Fact]
    public async Task Newer_confirmed_measurement_ends_the_period_and_the_next_lapse_is_reminded_again()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await AddAsync(api, z.Zone, StatusIds.WaterMeasurement.Confirmed, LocalMorning(Today.AddMonths(-2)));
        Assert.Equal(1, await RunAsync(api, Today));

        // Measured after the reminder: a new period starts, on time until a month later.
        await AddAsync(api, z.Zone, StatusIds.WaterMeasurement.Confirmed, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(0, await RunAsync(api, Today.AddDays(2)));
        Assert.Equal(1, await RunAsync(api, Today.AddMonths(1).AddDays(2)));

        Assert.Equal(2, (await RemindersAsync(api, z.Zone)).Count(n => n.RecipientAccountId == z.OwnerAccount.Id));
    }

    private static DateTimeOffset LocalMorning(DateOnly date) => new(date.ToDateTime(new TimeOnly(8, 0)), Local);

    private static Task AddAsync(WaterApiFactory api, FarmingZone zone, int statusId, DateTimeOffset measuredAt) =>
        api.UseDbAsync(db => db.WaterMeasurements.Add(new WaterMeasurement
        {
            ZoneId = zone.Id,
            MeasuredAt = measuredAt,
            Ph = 7.5m,
            Salinity = 20m,
            ClientOperationId = Guid.NewGuid(),
            StatusId = statusId,
        }));

    private static async Task<int> RunAsync(WaterApiFactory api, DateOnly today)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, CancellationToken.None);
    }

    private static Task<List<Notification>> RemindersAsync(WaterApiFactory api, FarmingZone zone) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordCode == zone.Code)
            .ToListAsync());
}
