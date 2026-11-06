using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// On SQL Server: the reminder's latest-measurement query, the latest-active lookup of a void and the stored offset of a
/// time sent without one translate and behave as on the in-memory provider.
/// </summary>
public class WaterOverdueRearmSqlServerTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [SqlServerFact]
    public async Task Same_day_correction_sends_no_second_reminder_and_voiding_the_answer_re_arms_it_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("rearm-owner", Password);
        var owner = await LoginAsync(api, "rearm-owner");
        var measuredAt = DateTimeOffset.UtcNow.AddDays(-40);
        var reminded = await seeded.RecordAsync(owner, 7m, 20m, measuredAt: measuredAt);
        Assert.Equal(1, await RunAsync(api, Today));

        var corrected = await owner.CorrectAsync(reminded, 7.2m, 20m, measuredAt: measuredAt);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(0, await RunAsync(api, Today.AddDays(1)));

        var answer = await seeded.RecordAsync(owner, 7m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await owner.VoidAsync(answer)).StatusCode);
        await using (var db = database.CreateContext())
        {
            Assert.Null(await db.FarmingZones.Where(z => z.Id == seeded.Zone.Id).Select(z => z.WaterOverdueRemindedAt).SingleAsync());
        }

        Assert.Equal(1, await RunAsync(api, Today.AddDays(1)));
        await using var check = database.CreateContext();
        Assert.Equal(2, await check.Notifications.CountAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordCode == seeded.Zone.Code));
    }

    // Two Active measurements taken at the same time: the one saved later is the zone's latest, every time.
    [SqlServerFact]
    public async Task Measurements_taken_at_the_same_time_are_ordered_by_the_time_they_were_saved_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        var measuredAt = DateTimeOffset.UtcNow.AddDays(-3);
        var first = Active(seeded, measuredAt, "first");
        var second = Active(seeded, measuredAt, "second");
        await using (var db = database.CreateContext())
        {
            // The later one is inserted first, so the insert order cannot decide.
            db.WaterMeasurements.Add(second);
            await db.SaveChangesAsync();
            db.WaterMeasurements.Add(first);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE [WaterMeasurements] SET [CreatedAt] = {measuredAt.AddHours(1)} WHERE [Id] = {first.Id}");
            await db.Database.ExecuteSqlAsync($"UPDATE [WaterMeasurements] SET [CreatedAt] = {measuredAt.AddHours(2)} WHERE [Id] = {second.Id}");
        }

        await using var api = new SqlServerApiFactory(database);
        using var scope = api.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWaterMeasurementRepository>();

        Assert.Equal(new[] { second.Id, first.Id }, (await repository.ListLatestActiveAsync(seeded.Zone.Id, 2, default)).Select(m => m.Id));
        Assert.Equal(second.Id, (await repository.ListInUseZonesWithLatestActiveAsync(default)).Single(z => z.Zone.Id == seeded.Zone.Id).LatestId);
    }

    [SqlServerFact]
    public async Task Time_without_offset_is_stored_as_vietnam_time_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("time-owner", Password);
        var owner = await LoginAsync(api, "time-owner");
        var day = Today.AddDays(-1);

        await seeded.RecordAsync(owner, 7m, 20m, measuredAt: $"{day:yyyy-MM-dd}T08:00:00");

        await using var db = database.CreateContext();
        var stored = await db.WaterMeasurements.Where(m => m.ZoneId == seeded.Zone.Id).Select(m => m.MeasuredAt).SingleAsync();
        Assert.Equal(new DateTimeOffset(day.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero), stored);
        Assert.Equal(BusinessClock.UtcOffset, stored.Offset);
    }

    private static WaterMeasurement Active(WaterSqlServerZone seeded, DateTimeOffset measuredAt, string photo) => new()
    {
        ZoneId = seeded.Zone.Id,
        RaftId = seeded.Raft.Id,
        MeasuredAt = measuredAt,
        Ph = 7.5m,
        Salinity = 20m,
        Temperature = 25m,
        PhotoUrl = photo,
        ClientOperationId = Guid.NewGuid(),
    };

    private static async Task<int> RunAsync(SqlServerApiFactory api, DateOnly today)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, CancellationToken.None);
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
