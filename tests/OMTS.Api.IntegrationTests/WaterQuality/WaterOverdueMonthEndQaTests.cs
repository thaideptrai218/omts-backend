using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA (OMTS-214 item 4, date-dependent tests): the overdue job at month ends, with pinned dates. "One calendar
/// month" after the last day of a short month is the same day number of the next month (30/09 → 30/10), so
/// <c>today.AddMonths(-1)</c> is NOT "exactly one month ago" on the 29th–31st: 31/10 minus one month is 30/09,
/// which is already overdue on 31/10. A test that builds its on-time measurement that way fails on such days.
/// </summary>
public class WaterOverdueMonthEndQaTests
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);

    [Theory]
    [InlineData("2026-09-30", "2026-10-30", "2026-10-31")]
    [InlineData("2026-01-31", "2026-02-28", "2026-03-01")]
    [InlineData("2026-02-28", "2026-03-28", "2026-03-29")]
    public async Task A_measurement_is_on_time_until_the_same_day_of_the_next_month_and_overdue_the_day_after(
        string measured, string lastOnTimeDay, string firstOverdueDay)
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        await api.UseDbAsync(db => db.WaterMeasurements.Add(new WaterMeasurement
        {
            ZoneId = z.Zone.Id,
            MeasuredAt = new DateTimeOffset(DateOnly.Parse(measured).ToDateTime(new TimeOnly(8, 0)), Local),
            Ph = 7.5m,
            Salinity = 20m,
            ClientOperationId = Guid.NewGuid(),
            StatusId = StatusIds.WaterMeasurement.Confirmed,
        }));

        var onTime = await RunAsync(api, DateOnly.Parse(lastOnTimeDay));
        var overdue = await RunAsync(api, DateOnly.Parse(firstOverdueDay));

        Assert.Equal(0, onTime);
        Assert.Equal(1, overdue);
        Assert.True(await api.QueryAsync(db => db.Notifications.AnyAsync(
            n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordCode == z.Zone.Code)));
    }

    private static async Task<int> RunAsync(WaterApiFactory api, DateOnly today)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, CancellationToken.None);
    }
}
