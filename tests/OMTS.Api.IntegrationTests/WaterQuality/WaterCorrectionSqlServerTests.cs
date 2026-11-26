using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// NFR-09 and NFR-13 on a real SQL Server: parallel requests on one measurement have no version check, so nothing answers 500
/// or MSG06, and the outcome is as if they ran one after the other: one void goes through, the others find it voided.
/// </summary>
[Collection(WaterRaceTestCollection.Name)]
public class WaterCorrectionSqlServerTests
{
    [SqlServerFact]
    public async Task Parallel_voids_end_voided_once_and_never_answer_500_or_MSG06()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await LoginAsync(api, await NewOwnerAsync(api));
        var id = await seeded.RecordAsync(owner, 9m, 20m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.VoidAsync(id)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-GEN-09", await refused.ErrorCodeAsync());
        }

        await using var db = database.CreateContext();
        var stored = await db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id);
        Assert.Equal(StatusIds.WaterMeasurement.Voided, stored.StatusId);
        var chain = await ChainAsync(db, id, "WaterMeasurement.Voided");
        Assert.Single(chain);
        Assert.Equal(new Values(stored.Ph, stored.Salinity, stored.Temperature, stored.RaftId, stored.MeasuredAt), chain[0].After);
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
