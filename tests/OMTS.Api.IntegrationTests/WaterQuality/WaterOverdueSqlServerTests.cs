using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// On SQL Server: the cycle port, the latest-measurement query, the recipients of GFR-04 and the stored offset of a time
/// sent without one translate and behave as on the in-memory provider.
/// </summary>
public class WaterOverdueSqlServerTests
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [SqlServerFact]
    public async Task A_due_zone_reminds_its_zone_manager_again_after_a_month_and_a_void_moves_the_base_back_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var (owner, ownerAccount) = await OwnerAsync(api);
        var manager = await api.AddAccountAsync($"m{NewCode()}", NewPassword(), SubjectType.Staff);
        await api.GrantAsync(manager, StaffRole.ZoneManager, seeded.Zone.Id);
        await AddOpenCycleAsync(database, seeded.Raft.Id, Today.AddMonths(-6));
        var measured = DateTimeOffset.UtcNow.AddDays(-40);
        await seeded.RecordAsync(owner, 7m, 20m, measuredAt: measured);
        var recent = await seeded.RecordAsync(owner, 7.1m, 20m);

        Assert.Equal(0, await api.RemindOnAsync(Today));
        Assert.Equal(HttpStatusCode.OK, (await owner.VoidAsync(recent)).StatusCode);
        Assert.Equal(1, await api.RemindOnAsync(Today));
        Assert.Equal(0, await api.RemindOnAsync(Today));
        Assert.Equal(1, await api.RemindOnAsync(Today.AddMonths(1)));

        await using var db = database.CreateContext();
        var reminders = await db.Notifications.AsNoTracking().Where(n => n.Type == NotificationTypes.WaterMeasurementOverdue).ToListAsync();
        Assert.Equal(2, reminders.Count);
        Assert.All(reminders, n => Assert.Equal(manager.Id, n.RecipientAccountId));
        Assert.DoesNotContain(reminders, n => n.RecipientAccountId == ownerAccount);
        Assert.All(reminders, n => Assert.Contains($"since {WaterOverdueTestKit.Named(BusinessClock.ToBusinessDate(measured))}.", n.Body));
    }

    // 15/05 17:30 UTC is 16/05 00:30 in Vietnam: the start date is the Vietnam date. Closed cycles do not count.
    [SqlServerFact]
    public async Task The_earliest_open_start_is_a_vietnam_date_per_zone_and_closed_cycles_are_left_out_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        var closedOnly = new FarmingZone { CooperativeId = seeded.Zone.CooperativeId, Code = NewCode(), Name = "Closed" };
        var closedRaft = new Raft { Zone = closedOnly, Code = $"B-{NewCode()}" };
        var secondRaft = new Raft { ZoneId = seeded.Zone.Id, Code = $"B-{NewCode()}" };
        await using (var db = database.CreateContext())
        {
            db.Rafts.AddRange(closedRaft, secondRaft);
            await db.SaveChangesAsync();
            db.CultivationCycles.Add(new CultivationCycle { RaftId = seeded.Raft.Id, OpenedAt = new DateTimeOffset(2026, 5, 15, 17, 30, 0, TimeSpan.Zero) });
            db.CultivationCycles.Add(WaterOverdueTestKit.OpenCycle(secondRaft.Id, new DateOnly(2026, 7, 1)));
            db.CultivationCycles.Add(WaterOverdueTestKit.ClosedCycle(closedRaft.Id, new DateOnly(2026, 1, 1)));
            await db.SaveChangesAsync();
        }

        await using var api = new SqlServerApiFactory(database);
        using var scope = api.Services.CreateScope();

        var starts = await scope.ServiceProvider.GetRequiredService<ICultivationCycleQuery>().EarliestOpenStartByZoneAsync(default);

        Assert.Equal(new DateOnly(2026, 5, 16), Assert.Single(starts).Value);
        Assert.Equal(seeded.Zone.Id, starts.Single().Key);
    }

    // A Zone Manager at All zones counts for every zone; only a zone with none falls back to the Owners.
    [SqlServerFact]
    public async Task Zone_managers_or_the_owners_are_found_with_all_zones_pairs_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync($"o{NewCode()}", NewPassword());
        var accountant = await api.AddAccountAsync($"a{NewCode()}", NewPassword(), SubjectType.Staff);
        await api.GrantAsync(accountant, StaffRole.Accountant, seeded.Zone.Id);
        using var scope = api.Services.CreateScope();
        var recipients = scope.ServiceProvider.GetRequiredService<IZoneRecipientQuery>();

        Assert.Equal([owner.Id], await recipients.GetZoneManagersOrOwnersAsync(seeded.Zone.Id, default));

        var allZones = await api.AddAccountAsync($"m{NewCode()}", NewPassword(), SubjectType.Staff);
        await api.GrantAsync(allZones, StaffRole.ZoneManager, zoneId: null);

        Assert.Equal([allZones.Id], await recipients.GetZoneManagersOrOwnersAsync(seeded.Zone.Id, default));
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
        Assert.Equal(second.Id, (await repository.LatestActiveByZoneAsync(default))[seeded.Zone.Id].Id);
    }

    [SqlServerFact]
    public async Task Time_without_offset_is_stored_as_vietnam_time_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var (owner, _) = await OwnerAsync(api);
        var day = Today.AddDays(-1);

        await seeded.RecordAsync(owner, 7m, 20m, measuredAt: $"{day:yyyy-MM-dd}T08:00:00");

        await using var db = database.CreateContext();
        var stored = await db.WaterMeasurements.Where(m => m.ZoneId == seeded.Zone.Id).Select(m => m.MeasuredAt).SingleAsync();
        Assert.Equal(new DateTimeOffset(day.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero), stored);
        Assert.Equal(BusinessClock.UtcOffset, stored.Offset);
    }

    private static async Task AddOpenCycleAsync(SqlServerTestDatabase database, Guid raftId, DateOnly start)
    {
        await using var db = database.CreateContext();
        db.CultivationCycles.Add(WaterOverdueTestKit.OpenCycle(raftId, start));
        await db.SaveChangesAsync();
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

    private static async Task<(HttpClient Client, Guid AccountId)> OwnerAsync(SqlServerApiFactory api)
    {
        var secret = NewPassword();
        var account = await api.AddAccountAsync($"o{NewCode()}", secret);
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, account.Username, secret);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, account.Id);
    }
}
