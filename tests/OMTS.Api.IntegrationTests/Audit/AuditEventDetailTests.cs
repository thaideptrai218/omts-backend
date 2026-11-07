using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>ACCOUNT-10 steps 4–5 and 10.1: an event with its values before and after, its record, and the filter values.</summary>
public class AuditEventDetailTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task An_event_shows_the_values_before_and_after_the_change_and_the_reason()
    {
        var (owner, client) = await factory.OwnerAsync();
        var staff = await factory.AddAccountAsync($"s{NewCode()}", NewPassword(), SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, null);

        var locked = await client.PostAsJsonAsync($"/api/accounts/{staff.Id}/lock", new { reason = "Left the cooperative" });
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var listed = Assert.Single((await client.SearchAsync($"recordCode={staff.Username}&action=Account.StatusChanged"))
            .GetProperty("page").GetProperty("items").EnumerateArray());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/audit-events/{listed.GetProperty("id").GetGuid()}");

        Assert.Equal("ACTIVE", detail.GetProperty("before").GetProperty("Status").GetString());
        Assert.Equal("LOCKED", detail.GetProperty("after").GetProperty("Status").GetString());
        Assert.Equal("Left the cooperative", detail.GetProperty("reason").GetString());
        Assert.Equal("Account", detail.GetProperty("recordType").GetString());
        Assert.Equal(staff.Id, detail.GetProperty("recordId").GetGuid());
        Assert.Equal(owner.Id, detail.GetProperty("actor").GetProperty("accountId").GetGuid());
        Assert.Equal(listed.GetProperty("occurredAt").GetDateTimeOffset(), detail.GetProperty("occurredAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Stored_values_are_shown_as_json_and_an_event_without_values_shows_none()
    {
        var (_, client) = await factory.OwnerAsync();
        var withValues = Event($"{NewTag()}-1", DateTimeOffset.UtcNow);
        withValues.Before = """{"Lines":[{"Item":"Phao","Quantity":2.5}],"Note":null}""";
        withValues.After = """{"Lines":[],"Note":"Đã sửa"}""";
        var withoutValues = Event($"{NewTag()}-2", DateTimeOffset.UtcNow);
        var notJson = Event($"{NewTag()}-3", DateTimeOffset.UtcNow);
        notJson.Before = "not json";
        await factory.SeedAsync(withValues, withoutValues, notJson);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/audit-events/{withValues.Id}");
        var empty = await client.GetFromJsonAsync<JsonElement>($"/api/audit-events/{withoutValues.Id}");
        var unreadable = await client.GetFromJsonAsync<JsonElement>($"/api/audit-events/{notJson.Id}");

        Assert.Equal(2.5m, detail.GetProperty("before").GetProperty("Lines")[0].GetProperty("Quantity").GetDecimal());
        Assert.Equal("Đã sửa", detail.GetProperty("after").GetProperty("Note").GetString());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("before").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("after").ValueKind);
        Assert.Equal(JsonValueKind.Null, unreadable.GetProperty("before").ValueKind);
        Assert.Equal("NOT_FOUND", await (await client.GetAsync($"/api/audit-events/{Guid.NewGuid()}")).ErrorCodeAsync());
    }

    [Fact]
    public async Task The_record_of_an_event_opens_while_it_exists_and_msg_account_22_once_it_is_removed()
    {
        var (_, client) = await factory.OwnerAsync();
        var zone = await factory.AddZoneAsync(NewTag());
        var license = new ZoneComplianceDocument { DocumentType = "AQUACULTURE_LICENSE", DocumentNumber = NewTag() };
        await factory.UseDbAsync(db => db.ZoneComplianceDocuments.Add(license));
        var zoneEvent = Event(zone.Code, DateTimeOffset.UtcNow, "FarmingZone.Created", nameof(FarmingZone));
        zoneEvent.RecordId = zone.Id;
        // Licenses are written as "ZoneLicense", not by their entity name.
        var licenseEvent = Event(license.DocumentNumber, DateTimeOffset.UtcNow, "ZoneLicense.Created", "ZoneLicense");
        licenseEvent.RecordId = license.Id;
        await factory.SeedAsync(zoneEvent, licenseEvent);

        var opened = await client.GetFromJsonAsync<JsonElement>($"/api/audit-events/{zoneEvent.Id}/record");
        Assert.Equal((zoneEvent.Id, "FarmingZone", zone.Id, zone.Code), (
            opened.GetProperty("eventId").GetGuid(), opened.GetProperty("recordType").GetString(), opened.GetProperty("recordId").GetGuid(), opened.GetProperty("recordCode").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/audit-events/{licenseEvent.Id}/record")).StatusCode);

        await factory.UseDbAsync(db => db.FarmingZones.Remove(db.FarmingZones.Single(z => z.Id == zone.Id)));
        var gone = await client.GetAsync($"/api/audit-events/{zoneEvent.Id}/record");

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("MSG-ACCOUNT-22", await gone.ErrorCodeAsync());
        Assert.Equal($"Farming zone {zone.Code} no longer exists.", JsonDocument.Parse(await gone.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString());
        // The event and its values stay available (ACCOUNT-10.1.E1).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/audit-events/{zoneEvent.Id}")).StatusCode);
        Assert.Equal([zone.Code], Codes(await client.SearchAsync($"recordCode={zone.Code}&action=FarmingZone.Created")));
    }

    [Fact]
    public async Task An_event_without_a_record_or_of_a_type_that_no_longer_exists_has_no_record_to_open()
    {
        var (_, client) = await factory.OwnerAsync();
        var refused = Event($"{NewTag()}-REFUSED", DateTimeOffset.UtcNow, "Account.OwnerRightsRefused", "Account");
        var retired = Event($"{NewTag()}-RETIRED", DateTimeOffset.UtcNow, "RetiredThing.Created", "RetiredThing");
        retired.RecordId = Guid.NewGuid();
        await factory.SeedAsync(refused, retired);

        var noRecord = await client.GetAsync($"/api/audit-events/{refused.Id}/record");
        var noType = await client.GetAsync($"/api/audit-events/{retired.Id}/record");

        Assert.Equal("MSG-ACCOUNT-22", await noRecord.ErrorCodeAsync());
        Assert.Equal($"Account {refused.RecordCode} no longer exists.", JsonDocument.Parse(await noRecord.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString());
        Assert.Equal("MSG-ACCOUNT-22", await noType.ErrorCodeAsync());
        Assert.Equal("NOT_FOUND", await (await client.GetAsync($"/api/audit-events/{Guid.NewGuid()}/record")).ErrorCodeAsync());
    }

    [Fact]
    public async Task Facets_list_the_actions_record_types_and_people_of_the_log_once_each()
    {
        var (owner, client) = await factory.OwnerAsync();
        var employee = new Employee { CooperativeId = (await factory.EnsureCooperativeAsync()).Id, FullName = $"Employee {NewTag()}" };
        await factory.UseDbAsync(db => db.Employees.Add(employee));
        var tag = NewTag();
        await factory.SeedAsync(
            With(Event($"{tag}-1", DateTimeOffset.UtcNow, $"{tag}.Created", $"{tag}Record"), owner.Id, null, null),
            With(Event($"{tag}-2", DateTimeOffset.UtcNow.AddYears(-4), $"{tag}.Created", $"{tag}Record"), owner.Id, null, null),
            With(Event($"{tag}-3", DateTimeOffset.UtcNow, $"{tag}.Updated", $"{tag}Record"), null, employee.Id, null));

        var facets = await client.GetFromJsonAsync<JsonElement>("/api/audit-events/facets");

        var actions = facets.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal(actions.Count, actions.Distinct().Count());
        Assert.Single(actions, a => a == $"{tag}.Created");
        Assert.Single(actions, a => a == $"{tag}.Updated");
        Assert.Single(facets.GetProperty("recordTypes").EnumerateArray(), t => t.GetString() == $"{tag}Record");
        var people = facets.GetProperty("people").EnumerateArray().ToList();
        var ownerFacet = Assert.Single(people, p => p.GetProperty("accountId").ValueKind != JsonValueKind.Null && p.GetProperty("accountId").GetGuid() == owner.Id);
        Assert.Equal(("Account", owner.Username), (ownerFacet.GetProperty("kind").GetString(), ownerFacet.GetProperty("username").GetString()));
        var employeeFacet = Assert.Single(people, p => p.GetProperty("employeeId").ValueKind != JsonValueKind.Null && p.GetProperty("employeeId").GetGuid() == employee.Id);
        Assert.Equal(("Employee", employee.FullName), (employeeFacet.GetProperty("kind").GetString(), employeeFacet.GetProperty("name").GetString()));
        Assert.DoesNotContain(people, p => p.GetProperty("name").GetString() == "System");
    }
}
