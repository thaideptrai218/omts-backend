using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// QA of OMTS-216 PR 0.6 (ACCOUNT-10, BR-ACCOUNT-13): only an Active Cooperative Owner reads the audit log, and nobody
/// else learns from the answers whether an event or its record exists.
/// </summary>
public class AuditSearchAccessQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_locked_owner_is_refused_on_every_endpoint_and_cannot_log_in_again()
    {
        var password = NewPassword();
        var owner = await factory.AddAccountAsync(NewCode(), password);
        var client = await factory.LoginAsync(owner.Username, password);
        var seeded = Event($"{NewTag()}-1", DateTimeOffset.UtcNow);
        await factory.SeedAsync(seeded);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/audit-events/{seeded.Id}")).StatusCode);

        // The Owner account cannot be locked in OMTS (BR-ACCOUNT-10); the deployment team can, in the database.
        await factory.UpdateAsync<Account>(owner.Id, a => a.StatusId = StatusIds.Account.Locked);

        foreach (var endpoint in EndpointsOf(seeded.Id))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(endpoint)).StatusCode);
        }

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = owner.Username, password });
        Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);
    }

    // BR-ACCOUNT-06: after a login with a temporary password only Change password is offered, even to the Owner.
    [Fact]
    public async Task An_owner_who_must_change_the_password_first_cannot_search()
    {
        var password = NewPassword();
        var owner = await factory.AddAccountAsync(NewCode(), password, configure: a =>
        {
            a.MustChangePassword = true;
            a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow;
        });
        var client = await factory.LoginAsync(owner.Username, password);

        foreach (var endpoint in EndpointsOf(Guid.NewGuid()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(endpoint)).StatusCode);
        }
    }

    // The detail and the record of an existing event answer exactly as an unknown id to anyone but the Owner, also when
    // the query is unreadable: the role is checked before anything is read or bound.
    [Fact]
    public async Task Non_owners_get_the_same_answer_for_an_existing_event_an_unknown_one_and_a_bad_query()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var withRecord = With(Event(zone.Code, DateTimeOffset.UtcNow, "FarmingZone.Created", nameof(FarmingZone)), null, null, zone.Id);
        withRecord.RecordId = zone.Id;
        var removed = Event($"{NewTag()}-GONE", DateTimeOffset.UtcNow, "FarmingZone.Created", nameof(FarmingZone));
        removed.RecordId = Guid.NewGuid();
        await factory.SeedAsync(withRecord, removed);

        var callers = new List<(string Name, HttpClient Client)>
        {
            ("zone manager of the zone", await StaffAsync(StaffRole.ZoneManager, zone)),
            ("zone manager of all zones", await StaffAsync(StaffRole.ZoneManager, null)),
            ("accountant of all zones", await StaffAsync(StaffRole.Accountant, null)),
            ("trader", await AccountAsync(SubjectType.Trader)),
        };

        foreach (var (name, client) in callers)
        {
            var answers = new List<(string Path, int Status, string? Code, string? Title, string? Detail)>();
            foreach (var path in new[]
            {
                $"/api/audit-events/{withRecord.Id}", $"/api/audit-events/{removed.Id}", $"/api/audit-events/{Guid.NewGuid()}",
                $"/api/audit-events/{withRecord.Id}/record", $"/api/audit-events/{removed.Id}/record", $"/api/audit-events/{Guid.NewGuid()}/record",
                "/api/audit-events?from=not-a-date&page=x&actorAccountId=nope", $"/api/audit-events?zoneId={zone.Id}",
            })
            {
                var response = await client.GetAsync(path);
                var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                answers.Add((path, (int)response.StatusCode, Text(problem, "code"), Text(problem, "title"), Text(problem, "detail")));
            }

            Assert.All(answers, a => Assert.True(a.Status == 403 && a.Code == "MSG-GEN-08", $"{name} {a.Path}: {a.Status} {a.Code}"));
            Assert.Single(answers.Select(a => (a.Status, a.Code, a.Title, a.Detail)).Distinct());
        }

        // Anonymous callers get a bare 401 whatever the id.
        var anonymous = factory.CreateClient();
        foreach (var path in new[] { $"/api/audit-events/{withRecord.Id}/record", $"/api/audit-events/{Guid.NewGuid()}/record" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // BR-ACCOUNT-13: the Owner sees every event, whatever its zone and whoever acted.
    [Fact]
    public async Task The_owner_sees_the_events_of_every_zone_and_of_no_zone()
    {
        var (_, client) = await factory.OwnerAsync();
        var tag = NewTag();
        var (zoneA, zoneB) = (Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(
            With(Event($"{tag}-A", DateTimeOffset.UtcNow.AddMinutes(-3)), null, null, zoneA),
            With(Event($"{tag}-B", DateTimeOffset.UtcNow.AddMinutes(-2)), Guid.NewGuid(), null, zoneB),
            With(Event($"{tag}-N", DateTimeOffset.UtcNow.AddMinutes(-1)), null, Guid.NewGuid(), null));

        Assert.Equal([$"{tag}-N", $"{tag}-B", $"{tag}-A"], Codes(await client.SearchAsync($"recordCode={tag}")));
    }

    private static string[] EndpointsOf(Guid eventId) =>
        ["/api/audit-events", "/api/audit-events/facets", $"/api/audit-events/{eventId}", $"/api/audit-events/{eventId}/record"];

    private static string? Text(JsonElement problem, string name) =>
        problem.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<HttpClient> AccountAsync(SubjectType subjectType)
    {
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password, subjectType);
        return await factory.LoginAsync(account.Username, password);
    }

    private async Task<HttpClient> StaffAsync(StaffRole role, FarmingZone? zone)
    {
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await factory.GrantAsync(account, role, zone);
        return await factory.LoginAsync(account.Username, password);
    }
}
