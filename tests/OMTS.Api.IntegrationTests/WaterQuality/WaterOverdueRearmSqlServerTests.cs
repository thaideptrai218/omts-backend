using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// OMTS-214 on SQL Server: the reminder's latest-measurement query (with the correction flag), the latest-confirmed
/// lookup of a void and the stored offset of a time sent without one translate and behave as on the in-memory provider.
/// </summary>
public class WaterOverdueRearmSqlServerTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [SqlServerFact]
    public async Task Same_day_correction_sends_no_second_reminder_and_voiding_the_answer_re_arms_it_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("rearm-owner", Password);
        var owner = await LoginAsync(api, "rearm-owner");
        var measuredAt = DateTimeOffset.UtcNow.AddDays(-40);
        var reminded = await RecordAsync(owner, zone, new { clientOperationId = Guid.NewGuid(), measuredAt, ph = 7m, salinity = 20m, confirm = true });
        Assert.Equal(1, await RunAsync(api, Today));

        var corrected = await owner.CorrectAsync(reminded.Id(), 7.2m, 20m, recordVersion: 1, measuredAt: measuredAt);
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        Assert.Equal(0, await RunAsync(api, Today.AddDays(1)));

        var answer = await RecordAsync(owner, zone, new { clientOperationId = Guid.NewGuid(), measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5), ph = 7m, salinity = 20m, confirm = true });
        Assert.Equal(HttpStatusCode.OK, (await owner.VoidAsync(answer.Id(), 1)).StatusCode);
        await using (var db = database.CreateContext())
        {
            Assert.Null(await db.FarmingZones.Where(z => z.Id == zone.Id).Select(z => z.WaterOverdueRemindedAt).SingleAsync());
        }

        Assert.Equal(1, await RunAsync(api, Today.AddDays(1)));
        await using var check = database.CreateContext();
        Assert.Equal(2, await check.Notifications.CountAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordCode == zone.Code));
    }

    // Two Confirmed measurements taken at the same time: the later confirmation is the zone's latest, every time.
    [SqlServerFact]
    public async Task Measurements_taken_at_the_same_time_are_ordered_by_confirmation_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        var measuredAt = DateTimeOffset.UtcNow.AddDays(-3);
        var (first, second) = (Confirmed(zone, measuredAt, measuredAt.AddHours(1)), Confirmed(zone, measuredAt, measuredAt.AddHours(2)));
        await using (var db = database.CreateContext())
        {
            // The later confirmation is inserted first, so the insert order cannot decide.
            db.WaterMeasurements.AddRange(second, first);
            await db.SaveChangesAsync();
        }

        await using var api = new SqlServerApiFactory(database);
        using var scope = api.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWaterMeasurementRepository>();

        Assert.Equal(new[] { second.Id, first.Id }, (await repository.ListLatestConfirmedAsync(zone.Id, 2, default)).Select(m => m.Id));
        Assert.Equal(second.Id, (await repository.ListInUseZonesWithLatestConfirmedAsync(default)).Single(z => z.Zone.Id == zone.Id).LatestId);
    }

    [SqlServerFact]
    public async Task Time_without_offset_is_stored_as_vietnam_time_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("time-owner", Password);
        var owner = await LoginAsync(api, "time-owner");
        var day = Today.AddDays(-1);

        await RecordAsync(owner, zone, new { clientOperationId = Guid.NewGuid(), measuredAt = $"{day:yyyy-MM-dd}T08:00:00", ph = 7m, salinity = 20m, confirm = true });

        await using var db = database.CreateContext();
        var stored = await db.WaterMeasurements.Where(m => m.ZoneId == zone.Id).Select(m => m.MeasuredAt).SingleAsync();
        Assert.Equal(new DateTimeOffset(day.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero), stored);
        Assert.Equal(BusinessClock.UtcOffset, stored.Offset);
    }

    private static WaterMeasurement Confirmed(FarmingZone zone, DateTimeOffset measuredAt, DateTimeOffset confirmedAt) => new()
    {
        ZoneId = zone.Id,
        MeasuredAt = measuredAt,
        ConfirmedAt = confirmedAt,
        Ph = 7.5m,
        Salinity = 20m,
        ClientOperationId = Guid.NewGuid(),
        StatusId = StatusIds.WaterMeasurement.Confirmed,
    };

    private static async Task<JsonElement> RecordAsync(HttpClient owner, FarmingZone zone, object body)
    {
        var response = await owner.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<int> RunAsync(SqlServerApiFactory api, DateOnly today)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, CancellationToken.None);
    }

    private static async Task<FarmingZone> SeedZoneAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var zone = new FarmingZone { Cooperative = new Cooperative { FullName = "HTX Trung Nam" }, Code = NewCode(), Name = "Zone SQL" };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        return zone;
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
