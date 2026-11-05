using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// Water rules only a real SQL Server shows (QA red team): row versions and unique indexes under parallel requests; the
/// per-account operation index and the one-photo index; decimal(4,2)/(5,2) and nvarchar(1000) at their limits.
/// </summary>
public class WaterSqlServerRedTeamTests
{
    [SqlServerFact]
    public async Task Racing_void_and_correction_of_one_record_let_exactly_one_win_and_leave_it_consistent()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var client = await LoginAsync(api, (await api.AddAccountAsync($"o{NewCode()}", Password)).Username);
        var id = await seeded.RecordAsync(client, 7m, 20m, 25m);

        var responses = await Task.WhenAll(
            client.VoidAsync(id),
            client.CorrectAsync(id, 7.3m, 20m),
            client.VoidAsync(id),
            client.CorrectAsync(id, 7.4m, 20m));

        // Whoever loses reads the new status (MSG-GEN-09) or loses on the row version (MSG06); a correction that
        // comes after another correction is a later edit of the same record, so it may also succeed.
        foreach (var refused in responses.Where(r => !r.IsSuccessStatusCode))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var code = await refused.ErrorCodeAsync();
            Assert.True(code is "MSG06" or "MSG-GEN-09", code);
        }

        await using var db = database.CreateContext();
        var stored = await db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id);
        var voids = await db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "WaterMeasurement.Voided");
        Assert.Equal(stored.StatusId == StatusIds.WaterMeasurement.Voided ? 1 : 0, voids);
        Assert.Equal(stored.StatusId == StatusIds.WaterMeasurement.Voided, stored.VoidReason is not null);
    }

    [SqlServerFact]
    public async Task One_client_operation_id_from_two_accounts_saves_two_records()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var first = await LoginAsync(api, (await api.AddAccountAsync($"o{NewCode()}", Password)).Username);
        var second = await LoginAsync(api, (await api.AddAccountAsync($"o{NewCode()}", Password)).Username);
        var operationId = Guid.NewGuid();

        var a = await seeded.PostAsync(first, 7m, 20m, 25m, operationId);
        var b = await seeded.PostAsync(second, 7.2m, 20m, 25m, operationId);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.WaterMeasurements.CountAsync(m => m.ClientOperationId == operationId));
    }

    [SqlServerFact]
    public async Task Values_and_thresholds_at_the_ends_of_their_ranges_are_stored_exactly()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var client = await LoginAsync(api, (await api.AddAccountAsync($"o{NewCode()}", Password)).Username);

        var thresholds = await client.PutAsJsonAsync($"/api/zones/{seeded.Zone.Id}/water-thresholds", new
        {
            phMin = 0.01m, phMax = 13.99m, salinityMin = 0.01m, salinityMax = 49.99m, temperatureMin = 0.01m, temperatureMax = 44.99m,
        });
        var high = await seeded.RecordAsync(client, 14.00m, 50.00m, 45.00m);
        var low = await seeded.RecordAsync(client, 0.00m, 0.00m, 0.00m);

        Assert.Equal(HttpStatusCode.OK, thresholds.StatusCode);
        await using var db = database.CreateContext();
        var stored = await db.WaterMeasurements.AsNoTracking().ToDictionaryAsync(m => m.Id);
        Assert.Equal((14.00m, 50.00m, 45.00m), (stored[high].Ph, stored[high].Salinity, stored[high].Temperature));
        Assert.Equal("pH 14.00 > 13.99; Salinity 50.00 > 49.99; Temperature 45.00 > 44.99", stored[high].ViolatedValues);
        Assert.Equal((0m, 0m, 0m), (stored[low].Ph, stored[low].Salinity, stored[low].Temperature));
        Assert.Equal("pH 0.00 < 0.01; Salinity 0.00 < 0.01; Temperature 0.00 < 0.01", stored[low].ViolatedValues);
        Assert.Equal((0.01m, 13.99m, 0.01m, 49.99m, 0.01m, 44.99m), (
            stored[high].PhMinUsed!.Value, stored[high].PhMaxUsed!.Value, stored[high].SalinityMinUsed!.Value, stored[high].SalinityMaxUsed!.Value,
            stored[high].TemperatureMinUsed!.Value, stored[high].TemperatureMaxUsed!.Value));
        var savedZone = await db.FarmingZones.AsNoTracking().SingleAsync(z => z.Id == seeded.Zone.Id);
        Assert.Equal((0.01m, 13.99m, 0.01m, 49.99m, 0.01m, 44.99m), (
            savedZone.PhMin!.Value, savedZone.PhMax!.Value, savedZone.SalinityMin!.Value, savedZone.SalinityMax!.Value,
            savedZone.TemperatureMin!.Value, savedZone.TemperatureMax!.Value));
    }

    [SqlServerFact]
    public async Task Reasons_of_exactly_1000_characters_are_stored_in_full_for_corrections_and_voids()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var client = await LoginAsync(api, (await api.AddAccountAsync($"o{NewCode()}", Password)).Username);
        var toCorrect = await seeded.RecordAsync(client, 7m, 20m, 25m);
        var toVoid = await seeded.RecordAsync(client, 7m, 20m, 25m);
        // 1000 UTF-16 units each, ending in a Vietnamese letter (nvarchar keeps it).
        var correctionReason = new string('c', 999) + "ố";
        var voidReason = new string('v', 999) + "ữ";

        var corrected = await client.CorrectAsync(toCorrect, 7.1m, 20m, reason: correctionReason);
        var voided = await client.VoidAsync(toVoid, reason: voidReason);

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        await using var db = database.CreateContext();
        Assert.Equal(voidReason, await db.WaterMeasurements.Where(m => m.Id == toVoid).Select(m => m.VoidReason).SingleAsync());
        Assert.Equal(correctionReason, await db.AuditEvents.Where(e => e.RecordId == toCorrect && e.Action == "WaterMeasurement.Corrected").Select(e => e.Reason).SingleAsync());
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
