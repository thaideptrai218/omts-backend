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
/// Water rules only a real SQL Server shows (QA red team): row versions and unique indexes under parallel
/// confirms, recognitions and void/correct races; the per-account operation index; decimal(4,2)/(5,2) and
/// nvarchar(1000) at their limits.
/// </summary>
public class WaterSqlServerRedTeamTests
{
    [SqlServerFact]
    public async Task Parallel_confirms_of_one_draft_confirm_it_once_with_one_alert_per_recipient_and_one_audit_row()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync($"o{NewCode()}", Password);
        var secondOwner = await api.AddAccountAsync($"o{NewCode()}", Password);
        var client = await LoginAsync(api, owner.Username);
        var id = await RecordAsync(client, zone, 9.2m, 20m, confirm: false);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync($"/api/water-measurements/{id}/confirm", new { })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var code = await refused.ErrorCodeAsync();
            Assert.True(code is "MSG06" or "MSG-GEN-09", code);
        }

        await using var db = database.CreateContext();
        Assert.Equal(StatusIds.WaterMeasurement.Confirmed, await db.WaterMeasurements.Where(m => m.Id == id).Select(m => m.StatusId).SingleAsync());
        var alerts = await db.Notifications.Where(n => n.Type == "WATER_ALERT" && n.RelatedRecordId == id).ToListAsync();
        Assert.Single(alerts, n => n.RecipientAccountId == owner.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == secondOwner.Id);
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "WaterMeasurement.Confirmed"));
    }

    [SqlServerFact]
    public async Task Parallel_recognitions_of_one_client_operation_save_one_draft_and_answer_it_to_every_request()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        var storage = Path.Combine(Path.GetTempPath(), $"omts-water-sql-{Guid.NewGuid():N}");
        try
        {
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                ["Storage:Local:RootPath"] = storage,
                // Generated per test so no key-like literal is committed.
                ["Storage:Local:SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)),
            });
            var owner = await api.AddAccountAsync($"o{NewCode()}", Password);
            var client = await LoginAsync(api, owner.Username);
            var operationId = Guid.NewGuid();

            var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
                client.PostAsync($"/api/zones/{zone.Id}/water-measurements/recognize", WaterTestKit.PhotoForm(WaterTestKit.Photo("ph7.7_sal21"), operationId))));

            var ids = new HashSet<Guid>();
            foreach (var response in responses)
            {
                Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measurement").Id());
            }

            Assert.Single(ids);
            await using var db = database.CreateContext();
            Assert.Equal(1, await db.WaterMeasurements.CountAsync());
            Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "WaterMeasurement.Created"));
        }
        finally
        {
            if (Directory.Exists(storage))
            {
                Directory.Delete(storage, recursive: true);
            }
        }
    }

    [SqlServerFact]
    public async Task Racing_void_and_correction_of_one_version_let_exactly_one_win_and_leave_a_consistent_chain()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync($"o{NewCode()}", Password);
        var client = await LoginAsync(api, owner.Username);
        var id = await RecordAsync(client, zone, 7m, 20m, confirm: true);

        var responses = await Task.WhenAll(
            client.VoidAsync(id, 1),
            client.CorrectAsync(id, 7.3m, 20m, recordVersion: 1),
            client.VoidAsync(id, 1),
            client.CorrectAsync(id, 7.4m, 20m, recordVersion: 1));

        Assert.Single(responses, r => r.IsSuccessStatusCode);
        foreach (var refused in responses.Where(r => !r.IsSuccessStatusCode))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var code = await refused.ErrorCodeAsync();
            Assert.True(code is "MSG06" or "MSG-GEN-09", code);
        }

        await using var db = database.CreateContext();
        var status = await db.WaterMeasurements.Where(m => m.Id == id).Select(m => m.StatusId).SingleAsync();
        var successors = await db.WaterMeasurements.CountAsync(m => m.CorrectedFromMeasurementId == id);
        var actions = await db.AuditEvents.Where(e => e.RecordId == id && (e.Action == "WaterMeasurement.Voided" || e.Action == "WaterMeasurement.Corrected"))
            .Select(e => e.Action).ToListAsync();
        if (status == StatusIds.WaterMeasurement.Voided)
        {
            Assert.Equal(0, successors);
            Assert.Equal(["WaterMeasurement.Voided"], actions);
        }
        else
        {
            Assert.Equal(StatusIds.WaterMeasurement.Superseded, status);
            Assert.Equal(1, successors);
            Assert.Equal(["WaterMeasurement.Corrected"], actions);
        }
    }

    [SqlServerFact]
    public async Task One_client_operation_id_from_two_accounts_saves_two_records()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var first = await LoginAsync(api, (await api.AddAccountAsync($"o{NewCode()}", Password)).Username);
        var second = await LoginAsync(api, (await api.AddAccountAsync($"o{NewCode()}", Password)).Username);
        var operationId = Guid.NewGuid();

        var a = await RecordResponseAsync(first, zone, 7m, 20m, true, operationId);
        var b = await RecordResponseAsync(second, zone, 7.2m, 20m, true, operationId);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.WaterMeasurements.CountAsync(m => m.ClientOperationId == operationId));
    }

    [SqlServerFact]
    public async Task Values_and_thresholds_at_the_ends_of_their_ranges_are_stored_exactly()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync($"o{NewCode()}", Password);
        var client = await LoginAsync(api, owner.Username);

        var thresholds = await client.PutAsJsonAsync($"/api/zones/{zone.Id}/water-thresholds", new { phMin = 0.01m, phMax = 13.99m, salinityMin = 0.01m, salinityMax = 99.99m });
        var high = await RecordAsync(client, zone, 14.00m, 100.00m, confirm: true);
        var low = await RecordAsync(client, zone, 0.00m, 0.00m, confirm: true);

        Assert.Equal(HttpStatusCode.OK, thresholds.StatusCode);
        await using var db = database.CreateContext();
        var stored = await db.WaterMeasurements.AsNoTracking().ToDictionaryAsync(m => m.Id);
        Assert.Equal((14.00m, 100.00m), (stored[high].Ph!.Value, stored[high].Salinity!.Value));
        Assert.Equal("pH 14.00 > 13.99; Salinity 100.00 > 99.99", stored[high].ViolatedValues);
        Assert.Equal((0m, 0m), (stored[low].Ph!.Value, stored[low].Salinity!.Value));
        Assert.Equal("pH 0.00 < 0.01; Salinity 0.00 < 0.01", stored[low].ViolatedValues);
        Assert.Equal((0.01m, 13.99m, 0.01m, 99.99m),
            (stored[high].PhMinUsed!.Value, stored[high].PhMaxUsed!.Value, stored[high].SalinityMinUsed!.Value, stored[high].SalinityMaxUsed!.Value));
        var savedZone = await db.FarmingZones.AsNoTracking().SingleAsync(z => z.Id == zone.Id);
        Assert.Equal((0.01m, 13.99m, 0.01m, 99.99m), (savedZone.PhMin!.Value, savedZone.PhMax!.Value, savedZone.SalinityMin!.Value, savedZone.SalinityMax!.Value));
    }

    [SqlServerFact]
    public async Task Reasons_of_exactly_1000_characters_are_stored_in_full_for_corrections_and_voids()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync($"o{NewCode()}", Password);
        var client = await LoginAsync(api, owner.Username);
        var toCorrect = await RecordAsync(client, zone, 7m, 20m, confirm: true);
        var toVoid = await RecordAsync(client, zone, 7m, 20m, confirm: true);
        // 1000 UTF-16 units each, ending in a Vietnamese letter (nvarchar keeps it).
        var correctionReason = new string('c', 999) + "ố";
        var voidReason = new string('v', 999) + "ữ";

        var corrected = await client.CorrectAsync(toCorrect, 7.1m, 20m, recordVersion: 1, reason: correctionReason);
        var voided = await client.VoidAsync(toVoid, 1, reason: voidReason);

        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var newId = (await corrected.Content.ReadFromJsonAsync<JsonElement>()).Id();
        await using var db = database.CreateContext();
        Assert.Equal(correctionReason, await db.WaterMeasurements.Where(m => m.Id == newId).Select(m => m.CorrectionReason).SingleAsync());
        Assert.Equal(voidReason, await db.WaterMeasurements.Where(m => m.Id == toVoid).Select(m => m.VoidReason).SingleAsync());
        Assert.Equal(correctionReason, await db.AuditEvents.Where(e => e.RecordId == toCorrect && e.Action == "WaterMeasurement.Corrected").Select(e => e.Reason).SingleAsync());
    }

    private static async Task<FarmingZone> SeedZoneAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var zone = new FarmingZone
        {
            Cooperative = new Cooperative { FullName = "HTX Trung Nam" },
            Code = NewCode(),
            Name = "Zone SQL",
            WaterMeasurementPoint = "Cọc đo số 1",
            PhMin = 6.5m,
            PhMax = 8.5m,
            SalinityMin = 15m,
            SalinityMax = 30m,
        };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        return zone;
    }

    private static Task<HttpResponseMessage> RecordResponseAsync(HttpClient client, FarmingZone zone, decimal ph, decimal salinity, bool confirm, Guid operationId) =>
        client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", new
        {
            clientOperationId = operationId,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ph,
            salinity,
            confirm,
        });

    private static async Task<Guid> RecordAsync(HttpClient client, FarmingZone zone, decimal ph, decimal salinity, bool confirm)
    {
        var response = await RecordResponseAsync(client, zone, ph, salinity, confirm, Guid.NewGuid());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Id();
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
