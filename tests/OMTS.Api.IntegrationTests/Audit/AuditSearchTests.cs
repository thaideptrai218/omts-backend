using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>ACCOUNT-10 – who may search the audit log, the filters, the order and the pages.</summary>
public class AuditSearchTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly string[] Endpoints = ["/api/audit-events", "/api/audit-events/facets", $"/api/audit-events/{Guid.NewGuid()}", $"/api/audit-events/{Guid.NewGuid()}/record"];

    [Fact]
    public async Task Only_the_cooperative_owner_searches_the_audit_log()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var clients = new List<HttpClient>();
        foreach (var (subject, role) in new (SubjectType, StaffRole?)[] { (SubjectType.Staff, StaffRole.ZoneManager), (SubjectType.Staff, StaffRole.Accountant), (SubjectType.Trader, null) })
        {
            var password = NewPassword();
            var account = await factory.AddAccountAsync(NewCode(), password, subject);
            if (role is { } fixedRole)
            {
                // The Zone Manager holds a zone, the Accountant All zones: neither opens the audit log.
                await factory.GrantAsync(account, fixedRole, fixedRole == StaffRole.ZoneManager ? zone : null);
            }

            clients.Add(await factory.LoginAsync(account.Username, password));
        }

        foreach (var endpoint in Endpoints)
        {
            foreach (var client in clients)
            {
                var response = await client.GetAsync(endpoint);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }

            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(endpoint)).StatusCode);
        }

        var (_, owner) = await factory.OwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Endpoints[0])).StatusCode);
    }

    [Fact]
    public async Task Events_are_listed_newest_first_with_the_person_action_record_and_reason()
    {
        var (owner, client) = await factory.OwnerAsync();
        var employee = new Employee { CooperativeId = (await factory.EnsureCooperativeAsync()).Id, FullName = "Trần Thị Bích" };
        await factory.UseDbAsync(db => db.Employees.Add(employee));
        var tag = NewTag();
        var now = DateTimeOffset.UtcNow;
        var byOwner = With(Event($"{tag}-OWNER", now.AddHours(-3)), owner.Id, null, null);
        byOwner.Reason = "Wrong salinity typed";
        await factory.SeedAsync(
            byOwner,
            Event($"{tag}-SYSTEM", now.AddHours(-1)),
            With(Event($"{tag}-EMPLOYEE", now.AddHours(-2)), null, employee.Id, null));

        var result = await client.SearchAsync($"recordCode={tag}");

        Assert.Equal([$"{tag}-SYSTEM", $"{tag}-EMPLOYEE", $"{tag}-OWNER"], Codes(result));
        var items = result.GetProperty("page").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("System", items[0].GetProperty("actor").GetProperty("kind").GetString());
        Assert.Equal("System", items[0].GetProperty("actor").GetProperty("name").GetString());
        Assert.Equal("Employee", items[1].GetProperty("actor").GetProperty("kind").GetString());
        Assert.Equal("Trần Thị Bích", items[1].GetProperty("actor").GetProperty("name").GetString());
        Assert.Equal(employee.Id, items[1].GetProperty("actor").GetProperty("employeeId").GetGuid());
        var ownerActor = items[2].GetProperty("actor");
        Assert.Equal(("Account", owner.Id, owner.Username), (ownerActor.GetProperty("kind").GetString(), ownerActor.GetProperty("accountId").GetGuid(), ownerActor.GetProperty("username").GetString()));
        Assert.Equal("Wrong salinity typed", items[2].GetProperty("reason").GetString());
        Assert.Equal("Test.Updated", items[2].GetProperty("action").GetString());
        Assert.Equal("TestRecord", items[2].GetProperty("recordType").GetString());
        Assert.False(items[2].TryGetProperty("before", out _));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("message").ValueKind);
    }

    [Fact]
    public async Task Each_filter_narrows_the_list_and_the_filters_combine()
    {
        var (owner, client) = await factory.OwnerAsync();
        var staff = await factory.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        var (zoneA, zoneB, employeeId, tag) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewTag());
        var (created, updated, zoneType, raftType) = ($"{tag}.Created", $"{tag}.Updated", $"{tag}Zone", $"{tag}Raft");
        var now = DateTimeOffset.UtcNow;
        await factory.SeedAsync(
            With(Event($"{tag}-A", now.AddMinutes(-5), created, zoneType), owner.Id, null, zoneA),
            With(Event($"{tag}-B", now.AddMinutes(-4), updated, zoneType), staff.Id, null, zoneB),
            With(Event($"{tag}-C", now.AddMinutes(-3), created, raftType), null, null, zoneA),
            With(Event($"{tag}-D", now.AddMinutes(-2), updated, raftType), null, employeeId, null),
            // Contains the tag, but not at the start: the record code matches from the start only.
            With(Event($"X-{tag}-E", now.AddMinutes(-1), created, raftType), owner.Id, null, zoneB));

        Assert.Equal([$"X-{tag}-E", $"{tag}-C", $"{tag}-A"], Codes(await client.SearchAsync($"action={created}")));
        Assert.Equal([$"X-{tag}-E", $"{tag}-D", $"{tag}-C"], Codes(await client.SearchAsync($"recordType={raftType}")));
        Assert.Equal([$"{tag}-D", $"{tag}-C", $"{tag}-B", $"{tag}-A"], Codes(await client.SearchAsync($"recordCode={tag}")));
        Assert.Equal([$"{tag}-C", $"{tag}-A"], Codes(await client.SearchAsync($"zoneId={zoneA}")));
        Assert.Equal([$"{tag}-B"], Codes(await client.SearchAsync($"actorAccountId={staff.Id}")));
        Assert.Equal([$"{tag}-D"], Codes(await client.SearchAsync($"actorEmployeeId={employeeId}")));
        Assert.Equal([$"{tag}-C"], Codes(await client.SearchAsync($"system=true&recordCode={tag}")));
        Assert.Equal([$"X-{tag}-E", $"{tag}-C"], Codes(await client.SearchAsync($"action={created}&recordType={raftType}")));
        Assert.Equal([$"X-{tag}-E"], Codes(await client.SearchAsync($"actorAccountId={owner.Id}&zoneId={zoneB}")));
        Assert.Equal([$"{tag}-C"], Codes(await client.SearchAsync($"system=true&action={created}&recordType={raftType}&recordCode={tag}-&zoneId={zoneA}")));
        // system=false is no filter.
        Assert.Equal(4, Codes(await client.SearchAsync($"system=false&recordCode={tag}")).Count);
        Assert.Empty(Codes(await client.SearchAsync($"action={updated}&zoneId={zoneA}")));
    }

    [Fact]
    public async Task No_matching_event_answers_msg_gen_10()
    {
        var (_, client) = await factory.OwnerAsync();

        var result = await client.SearchAsync($"action={NewTag()}.Nothing");

        Assert.Equal(0, result.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Empty(Codes(result));
        Assert.Equal("MSG-GEN-10", result.GetProperty("messageCode").GetString());
        Assert.Equal("There is no data for the period and filters you chose. Please choose again.", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Pages_are_bounded_and_keep_events_of_the_same_instant_in_one_order()
    {
        var (_, client) = await factory.OwnerAsync();
        var tag = NewTag();
        var at = DateTimeOffset.UtcNow.AddMinutes(-10);
        await factory.SeedAsync(Enumerable.Range(1, 5).Select(i => Event($"{tag}-{i}", at)).ToArray());

        var pages = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            pages.AddRange(Codes(await client.SearchAsync($"recordCode={tag}&pageSize=2&page={page}")));
        }

        Assert.Equal(5, pages.Distinct().Count());
        Assert.Equal(pages, Codes(await client.SearchAsync($"recordCode={tag}&pageSize=5")));
        var first = await client.SearchAsync($"recordCode={tag}&page=0&pageSize=0");
        Assert.Equal((1, 1, 5), Paging(first));
        Assert.Equal((1, 100, 5), Paging(await client.SearchAsync($"recordCode={tag}&pageSize=1000")));
        var beyond = await client.SearchAsync($"recordCode={tag}&page=99");
        Assert.Empty(Codes(beyond));
        Assert.Equal(5, beyond.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, beyond.GetProperty("messageCode").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/audit-events?recordCode={tag}&page={int.MaxValue}&pageSize={int.MaxValue}")).StatusCode);
    }

    [Fact]
    public async Task Two_people_at_once_and_unreadable_filters_are_refused_with_msg_gen_01()
    {
        var (owner, client) = await factory.OwnerAsync();

        foreach (var (query, field) in new[]
        {
            ($"system=true&actorAccountId={owner.Id}", "Person"),
            ($"actorAccountId={owner.Id}&actorEmployeeId={Guid.NewGuid()}", "Person"),
            ($"action={new string('a', 101)}", "Action"),
            ($"recordType={new string('a', 101)}", "Record type"),
            ($"recordCode={new string('a', 101)}", "Record code"),
            ("actorAccountId=not-a-guid", "actorAccountId"),
            ("system=maybe", "system"),
        })
        {
            var response = await client.GetAsync($"/api/audit-events?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
            Assert.Equal(field, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
        }
    }

    [Fact]
    public async Task Searching_changes_no_data_and_events_are_never_edited_or_deleted()
    {
        var (_, client) = await factory.OwnerAsync();
        var seeded = Event($"{NewTag()}-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        await factory.SeedAsync(seeded);
        var before = await factory.QueryAsync(db => db.AuditEvents.CountAsync());

        await client.SearchAsync($"recordCode={seeded.RecordCode}");
        await client.GetAsync($"/api/audit-events/{seeded.Id}");
        await client.GetAsync($"/api/audit-events/{seeded.Id}/record");
        await client.GetAsync("/api/audit-events/facets");
        var delete = await client.DeleteAsync($"/api/audit-events/{seeded.Id}");
        var put = await client.PutAsync($"/api/audit-events/{seeded.Id}", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(before, await factory.QueryAsync(db => db.AuditEvents.CountAsync()));
        Assert.Equal(seeded.RecordCode, await factory.QueryAsync(db => db.AuditEvents.Where(e => e.Id == seeded.Id).Select(e => e.RecordCode).SingleAsync()));
    }

    private static (int Page, int PageSize, int Total) Paging(JsonElement result)
    {
        var page = result.GetProperty("page");
        return (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt32());
    }
}
