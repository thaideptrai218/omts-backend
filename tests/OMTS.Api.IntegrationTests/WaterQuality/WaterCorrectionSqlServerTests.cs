using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// NFR-09 and NFR-13 on a real SQL Server: parallel requests on one measurement have no version check, so nobody is refused
/// for it and nothing answers 500, and the outcome is as if they ran one after the other: the row holds one request's
/// values with their result, and the audit rows form one chain.
/// </summary>
public class WaterCorrectionSqlServerTests
{
    private static readonly DateTimeOffset FirstBodyTime = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());

    [SqlServerFact]
    public async Task Parallel_corrections_all_save_one_after_the_other_and_leave_the_whole_of_one_request()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await LoginAsync(api, await NewOwnerAsync(api));
        var otherOwner = await NewOwnerAsync(api);
        var otherRaft = await AddRaftAsync(database, seeded.Zone);
        var id = await seeded.RecordAsync(owner, 7m, 20m);
        var bodies = Enumerable.Range(0, 8).Select(i => Body(i, i % 2 == 0 ? seeded.Raft.Id : otherRaft)).ToList();

        var responses = await Task.WhenAll(bodies.Select(b => owner.PostAsJsonAsync($"/api/water-measurements/{id}/corrections", b.Json)));

        foreach (var response in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.WaterMeasurements.CountAsync());
        var stored = await db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id);
        var row = new Values(stored.Ph, stored.Salinity, stored.Temperature, stored.RaftId, stored.MeasuredAt);
        Assert.Contains(row, bodies.Select(b => b.Values));
        var evaluation = WaterThresholdEvaluator.Evaluate(stored.Ph, stored.Salinity, stored.Temperature, WaterThresholds.UsedBy(stored));
        Assert.Equal((evaluation.Exceeded, evaluation.ViolatedValues), (stored.ThresholdExceeded, stored.ViolatedValues));

        var chain = await ChainAsync(db, id, "WaterMeasurement.Corrected");
        Assert.Equal(bodies.Count, chain.Count);
        Assert.Equal(row, chain[^1].After);

        // Each correction was judged on the record its predecessor left, so the notices are those of that one order.
        var notices = chain.Select(c => WaterCorrectionNoticeRule.AfterCorrection(true, Outside(stored, c.Before), Outside(stored, c.After))).ToList();
        var received = await db.Notifications.AsNoTracking().Where(n => n.RecipientAccountId == otherOwner.Id && n.RelatedRecordId == id).ToListAsync();
        Assert.Equal(notices.Count(n => n == WaterCorrectionNotice.CorrectedAlert), received.Count(n => n.Type == NotificationTypes.WaterAlert));
        Assert.Equal(notices.Count(n => n == WaterCorrectionNotice.AlertWithdrawn), received.Count(n => n.Type == NotificationTypes.WaterAlertWithdrawn));
        // The pushes of the attempts that lost are dropped with them (EF Core reports a conflict apart from a failed save).
        var pushed = api.Services.GetRequiredService<FakePushSender>().Inbox.List(otherOwner.Id.ToString());
        Assert.Equal(received.Count, pushed.Count);
    }

    [SqlServerFact]
    public async Task Parallel_corrections_and_voids_end_voided_once_with_the_last_values_and_never_answer_500_or_MSG06()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await LoginAsync(api, await NewOwnerAsync(api));
        var id = await seeded.RecordAsync(owner, 7m, 20m);
        var bodies = Enumerable.Range(0, 6).Select(i => Body(i, seeded.Raft.Id)).ToList();

        var responses = await Task.WhenAll(bodies
            .Select(b => owner.PostAsJsonAsync($"/api/water-measurements/{id}/corrections", b.Json))
            .Concat(Enumerable.Range(0, 2).Select(_ => owner.VoidAsync(id))));

        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-GEN-09", await refused.ErrorCodeAsync());
        }

        await using var db = database.CreateContext();
        var stored = await db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id);
        Assert.Equal(StatusIds.WaterMeasurement.Voided, stored.StatusId);
        var chain = await ChainAsync(db, id, "WaterMeasurement.Corrected", "WaterMeasurement.Voided");
        Assert.Equal(responses.Count(r => r.StatusCode == HttpStatusCode.OK), chain.Count);
        Assert.Equal("WaterMeasurement.Voided", chain[^1].Action);
        Assert.Single(chain, c => c.Action == "WaterMeasurement.Voided");
        Assert.Equal(new Values(stored.Ph, stored.Salinity, stored.Temperature, stored.RaftId, stored.MeasuredAt), chain[^1].After);
    }

    [SqlServerFact]
    public async Task A_withdrawal_with_the_longest_reason_is_stored_in_full_with_its_parameters()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await LoginAsync(api, await NewOwnerAsync(api));
        var otherOwner = await NewOwnerAsync(api);
        var id = await seeded.RecordAsync(owner, 9m, 20m);
        var reason = new string('c', 999) + "ố";

        var voided = await owner.VoidAsync(id, reason: reason);

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        await using var db = database.CreateContext();
        var withdrawn = await db.Notifications.AsNoTracking().SingleAsync(n => n.RecipientAccountId == otherOwner.Id && n.Type == NotificationTypes.WaterAlertWithdrawn);
        Assert.EndsWith($"Reason: {reason}.", withdrawn.Body);
        Assert.Contains(reason, withdrawn.MessageParams);
    }

    private static IReadOnlyCollection<string> Outside(OMTS.Domain.Entities.Cultivation.WaterMeasurement stored, Values values) =>
        WaterThresholdEvaluator.Evaluate(values.Ph, values.Salinity, values.Temperature, WaterThresholds.UsedBy(stored)).ViolatedMetrics;

    /// <summary>Every step walked from the record as created: each one starts with the values the one before it ended with.</summary>
    private static async Task<List<Step>> ChainAsync(OMTS.Infrastructure.Persistence.OmtsDbContext db, Guid id, params string[] actions)
    {
        var events = await db.AuditEvents.AsNoTracking().Where(e => e.RecordId == id).ToListAsync();
        var current = Parse(events.Single(e => e.Action == "WaterMeasurement.Created").After!);
        var remaining = events.Where(e => actions.Contains(e.Action))
            .Select(e => new Step(e.Action, Parse(e.Before!), Parse(e.After!)))
            .ToList();
        var chain = new List<Step>();
        while (remaining.Count > 0)
        {
            var next = remaining.Find(s => s.Before == current);
            Assert.True(next is not null, $"No audit row starts from {current}: two saves overlapped.");
            remaining.Remove(next!);
            chain.Add(next!);
            current = next!.After;
        }

        return chain;
    }

    private static Values Parse(string snapshot)
    {
        using var json = JsonDocument.Parse(snapshot);
        var root = json.RootElement;
        return new Values(
            root.GetProperty("Ph").GetDecimal(), root.GetProperty("Salinity").GetDecimal(), root.GetProperty("Temperature").GetDecimal(),
            root.GetProperty("RaftId").GetGuid(), root.GetProperty("MeasuredAt").GetDateTimeOffset());
    }

    /// <summary>A request whose every field differs from the others', with values outside in some of them.</summary>
    private static (object Json, Values Values) Body(int i, Guid raftId)
    {
        var values = new Values(
            (i % 3 == 0 ? 9m : 7m) + (i / 10m),
            (i % 3 == 1 ? 31m : 20m) + i,
            25m + i,
            raftId,
            FirstBodyTime.AddMinutes(-i));
        return (new { raftId, measuredAt = values.MeasuredAt, ph = values.Ph, salinity = values.Salinity, temperature = values.Temperature, reason = $"Sai số {i}" }, values);
    }

    private static async Task<Guid> AddRaftAsync(SqlServerTestDatabase database, FarmingZone zone)
    {
        await using var db = database.CreateContext();
        var raft = new Raft { ZoneId = zone.Id, Code = $"B-{NewCode()}" };
        db.Rafts.Add(raft);
        await db.SaveChangesAsync();
        return raft.Id;
    }

    private static async Task<OwnerLogin> NewOwnerAsync(SqlServerApiFactory api)
    {
        var password = NewPassword();
        var account = await api.AddAccountAsync($"o{NewCode()}", password);
        return new OwnerLogin(account.Id, account.Username, password);
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, OwnerLogin owner)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, owner.Username, owner.Secret);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private sealed record OwnerLogin(Guid Id, string Username, string Secret);

    private sealed record Values(decimal Ph, decimal Salinity, decimal Temperature, Guid RaftId, DateTimeOffset MeasuredAt);

    private sealed record Step(string Action, Values Before, Values After);
}
