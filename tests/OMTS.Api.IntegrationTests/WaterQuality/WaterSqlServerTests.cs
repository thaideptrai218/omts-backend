using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>Water rules that only a real SQL Server shows: unique indexes and row versions under parallel requests, and the migration on old data.</summary>
public class WaterSqlServerTests
{
    // Migrations are regenerated on every rebase, so the one before the water migration is looked up, not named.
    private static string MigrationBeforeWater(DbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        return all[all.FindIndex(m => m.EndsWith("_AddWaterMeasurementOperations", StringComparison.Ordinal)) - 1];
    }

    [SqlServerFact]
    public async Task Parallel_re_sends_of_one_client_operation_save_one_record_and_one_alert_per_recipient()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync("water-owner", Password);
        var client = await LoginAsync(api, owner.Username);
        var body = new { clientOperationId = Guid.NewGuid(), measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5), ph = 9.2m, salinity = 20m, confirm = true };

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", body)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).Id());
        }

        Assert.Single(ids);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.WaterMeasurements.CountAsync());
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id && n.Type == "WATER_ALERT"));
    }

    [SqlServerFact]
    public async Task Parallel_corrections_of_one_version_let_one_win_and_answer_MSG06_to_the_others()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync("water-owner2", Password);
        var client = await LoginAsync(api, owner.Username);
        var created = await client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements",
            new { clientOperationId = Guid.NewGuid(), measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5), ph = 7m, salinity = 20m, confirm = true });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).Id();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => client.CorrectAsync(id, 7.1m + (i / 10m), 20m, recordVersion: 1)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG06", await refused.ErrorCodeAsync());
        }

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.WaterMeasurements.CountAsync(m => m.CorrectedFromMeasurementId == id));
        Assert.Equal(StatusIds.WaterMeasurement.Superseded, await db.WaterMeasurements.Where(m => m.Id == id).Select(m => m.StatusId).SingleAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "WaterMeasurement.Corrected"));
    }

    // "Applies cleanly on the dev database": measurements recorded before the migration get distinct operation ids.
    [SqlServerFact]
    public async Task Migration_gives_existing_measurements_their_own_operation_id()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeWater(db));
            var (cooperative, zone, account) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, StatusId, CreatedAt) VALUES ({cooperative}, N'HTX', 201, SYSDATETIMEOFFSET())");
            await db.Database.ExecuteSqlAsync($"INSERT INTO FarmingZones (Id, CooperativeId, Code, Name, StatusId, CreatedAt) VALUES ({zone}, {cooperative}, N'Z1', N'Zone 1', 101, SYSDATETIMEOFFSET())");
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt) VALUES ({account}, N'old', N'Old', N'x', N'Staff', 301, 0, 0, SYSDATETIMEOFFSET())");
            for (var i = 0; i < 2; i++)
            {
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO WaterMeasurements (Id, ZoneId, MeasuredAt, Ph, Salinity, InputSource, MeasuredByAccountId, ThresholdExceeded, RecordVersion, StatusId, CreatedAt) VALUES ({Guid.NewGuid()}, {zone}, SYSDATETIMEOFFSET(), 7.5, 20, N'Manual', {account}, 0, 1, 702, SYSDATETIMEOFFSET())");
            }

            await db.Database.MigrateAsync();

            var operations = await db.WaterMeasurements.Select(m => m.ClientOperationId).ToListAsync();
            Assert.Equal(2, operations.Distinct().Count());
            Assert.DoesNotContain(Guid.Empty, operations);
            Assert.True(await db.Statuses.AnyAsync(s => s.Id == StatusIds.WaterMeasurement.Voided && s.Code == "VOIDED" && s.IsFinal));
        }
    }

    private static async Task<FarmingZone> SeedZoneAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var cooperative = new Cooperative { FullName = "HTX Trung Nam" };
        var zone = new FarmingZone { Cooperative = cooperative, Code = NewCode(), Name = "Zone SQL", PhMin = 6.5m, PhMax = 8.5m };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        return zone;
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
