using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// ACCOUNT-10 test helpers. Tests of one class share a database, so each test tags its events with its own codes,
/// actions or accounts and searches with them.
/// </summary>
internal static class AuditTestKit
{
    /// <summary>A unique upper-case tag; record codes, actions and record types built from it belong to one test.</summary>
    public static string NewTag() => AcsTestKit.NewCode().ToUpperInvariant();

    public static DateOnly Today => TimeProvider.System.Today();

    /// <summary>The start of a Vietnam business day, as the search reads <c>from</c>/<c>to</c>.</summary>
    public static DateTimeOffset StartOf(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);

    /// <summary>A fresh Cooperative Owner and a client logged in as that Owner.</summary>
    public static async Task<(Account Owner, HttpClient Client)> OwnerAsync(this AuthApiFactory factory)
    {
        var password = AcsTestKit.NewPassword();
        var owner = await factory.AddAccountAsync(AcsTestKit.NewCode(), password);
        return (owner, await factory.LoginAsync(owner.Username, password));
    }

    /// <summary>Writes the events as they are, without the audit service (times, actors and values chosen by the test).</summary>
    public static Task SeedAsync(this AuthApiFactory factory, params AuditEvent[] events) =>
        factory.UseDbAsync(db => db.AuditEvents.AddRange(events));

    public static AuditEvent Event(string code, DateTimeOffset at, string action = "Test.Updated", string recordType = "TestRecord") =>
        new() { RecordCode = code, OccurredAt = at, Action = action, RecordType = recordType };

    /// <summary>Sets who acted (neither: the system) and the zone of the event.</summary>
    public static AuditEvent With(AuditEvent e, Guid? accountId, Guid? employeeId, Guid? zoneId)
    {
        e.ActorAccountId = accountId;
        e.ActorEmployeeId = employeeId;
        e.ZoneId = zoneId;
        return e;
    }

    /// <summary>GET /api/audit-events with the query string; the search must answer 200.</summary>
    public static async Task<JsonElement> SearchAsync(this HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/audit-events?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The record codes of the page, in the order listed.</summary>
    public static List<string> Codes(JsonElement result) =>
        result.GetProperty("page").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("recordCode").GetString()!).ToList();

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd");
}
