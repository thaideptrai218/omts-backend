using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>Water rules that only a real SQL Server shows: unique indexes and row versions under parallel requests.</summary>
public class WaterSqlServerTests
{
    [SqlServerFact]
    public async Task Parallel_re_sends_of_one_client_operation_save_one_record_and_one_alert_per_recipient()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync("water-owner", Password);
        var otherOwner = await api.AddAccountAsync("water-owner-other", Password);
        var client = await LoginAsync(api, owner.Username);
        var body = new
        {
            clientOperationId = Guid.NewGuid(),
            raftId = seeded.Raft.Id,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ph = 9.2m,
            salinity = 20m,
            temperature = 25m,
            photoKey = await client.PhotoKeyAsync(seeded.Zone.Id),
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync($"/api/zones/{seeded.Zone.Id}/water-measurements", body)));

        foreach (var response in responses)
        {
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).Id());
        }

        Assert.Single(ids);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.WaterMeasurements.CountAsync());
        // The recording Owner is not alerted about their own measurement (PM decision 03/11); the other Owner is, once.
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id && n.Type == "WATER_ALERT"));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.RecipientAccountId == otherOwner.Id && n.Type == "WATER_ALERT"));
    }

    // Two different operations naming one photo race past the check: the unique index keeps one, the other is told about the photo.
    [SqlServerFact]
    public async Task Parallel_records_with_one_photo_save_one_measurement_and_refuse_the_others_on_the_photo()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var client = await LoginAsync(api, (await api.AddAccountAsync("water-owner3", Password)).Username);
        var photoKey = await client.PhotoKeyAsync(seeded.Zone.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.PostAsJsonAsync($"/api/zones/{seeded.Zone.Id}/water-measurements", new
        {
            raftId = seeded.Raft.Id,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ph = 7m,
            salinity = 20m,
            temperature = 25m,
            photoKey,
        })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("Photo", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.WaterMeasurements.CountAsync());
    }

    // No version check for water (NFR-09): parallel corrections of one record change it one at a time and all save.
    [SqlServerFact]
    public async Task Parallel_corrections_of_one_record_keep_one_row_and_all_answer_saved()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var client = await LoginAsync(api, (await api.AddAccountAsync("water-owner2", Password)).Username);
        var id = await seeded.RecordAsync(client, 7m, 20m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => client.CorrectAsync(id, 7.1m + (i / 10m), 20m)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.WaterMeasurements.CountAsync());
        Assert.Equal(StatusIds.WaterMeasurement.Active, await db.WaterMeasurements.Where(m => m.Id == id).Select(m => m.StatusId).SingleAsync());
        Assert.Equal(responses.Length, await db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "WaterMeasurement.Corrected"));
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
