using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-02 rules and error bullets: validation, unique code, status transitions, audit, list, badge and map.</summary>
public class FarmingZoneRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Create_lists_every_invalid_field_and_saves_nothing()
    {
        var owner = await factory.OwnerAsync();
        var zonesBefore = await factory.QueryAsync(db => db.FarmingZones.CountAsync());

        var response = await owner.Client.PostAsJsonAsync("/api/zones", new ZoneBody(
            "  ", null, new string('x', 501), 10m, null, 0m, new string('w', 201)));

        var fields = await response.InvalidFieldsAsync();
        Assert.Equal(["Code", "Name", "Location", "Longitude", "Area", "Water measurement point"], fields);
        Assert.Equal(zonesBefore, await factory.QueryAsync(db => db.FarmingZones.CountAsync()));

        // Coordinates are required on create and out-of-range values are refused.
        Assert.Equal(["Latitude", "Longitude"], await (await owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody() with { Latitude = null, Longitude = null })).InvalidFieldsAsync());
        Assert.Equal(["Latitude", "Longitude"], await (await owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody() with { Latitude = -90.000001m, Longitude = 180.000001m })).InvalidFieldsAsync());
        Assert.Equal(["Code"], await (await owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody(new string('C', 51)))).InvalidFieldsAsync());
        Assert.Equal(["Name"], await (await owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody() with { Name = new string('n', 201) })).InvalidFieldsAsync());
        Assert.Equal(["Area"], await (await owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody() with { Area = -1 })).InvalidFieldsAsync());

        // The longest allowed values and the coordinate bounds are accepted.
        var longest = await owner.Client.CreateZoneAsync(new ZoneBody(
            NewCode() + new string('C', 40), new string('n', 200), new string('l', 500), -90m, 180m, 0.0001m, new string('w', 200)));
        Assert.Equal(50, longest.Text("code").Length);
        Assert.Equal(zonesBefore + 1, await factory.QueryAsync(db => db.FarmingZones.CountAsync()));
    }

    [Fact]
    public async Task Code_is_trimmed_and_unique_ignoring_case_on_create_and_edit()
    {
        var owner = await factory.OwnerAsync();
        var code = $"ZA-{NewCode()}";
        var first = await owner.Client.CreateZoneAsync(NewZoneBody($"  {code}  "));
        Assert.Equal(code, first.Text("code"));

        var duplicate = await owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody($" {code.ToLowerInvariant()} "));
        var problem = await duplicate.ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-02", problem.Text("code"));
        Assert.Equal(first.Id(), problem.GetProperty("existingZoneId").GetGuid());

        var second = await owner.Client.CreateZoneAsync();
        var renamed = await owner.Client.PutAsJsonAsync($"/api/zones/{second.Id()}", NewZoneBody(code.ToUpperInvariant()) with { RowVersion = second.Text("rowVersion") });
        var editProblem = await renamed.ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-02", editProblem.Text("code"));
        Assert.Equal(first.Id(), editProblem.GetProperty("existingZoneId").GetGuid());

        // Keeping its own code (in another case) is not a duplicate.
        var sameZone = await owner.Client.PutAsJsonAsync($"/api/zones/{first.Id()}", NewZoneBody(code.ToLowerInvariant()) with { RowVersion = first.Text("rowVersion") });
        Assert.Equal(code.ToLowerInvariant(), (await sameZone.ReadAsync(HttpStatusCode.OK)).Text("code"));
    }

    [Fact]
    public async Task Unknown_zone_is_404_for_reads_and_writes()
    {
        var owner = await factory.OwnerAsync();
        var unknown = Guid.NewGuid();
        var change = new { reason = (string?)null, rowVersion = "" };

        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/zones/{unknown}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PutAsJsonAsync($"/api/zones/{unknown}", NewZoneBody() with { RowVersion = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PostAsJsonAsync($"/api/zones/{unknown}/discontinue", change)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PostAsJsonAsync($"/api/zones/{unknown}/reactivate", change)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/zones/{unknown}/rafts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PostAsJsonAsync($"/api/zones/{unknown}/rafts", NewRaftBody())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/zones/{unknown}/licenses")).StatusCode);
    }

    [Fact]
    public async Task Discontinue_names_the_in_use_rafts_and_both_transitions_are_audited_with_their_reason()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var rafts = new List<JsonElement>();
        foreach (var code in new[] { "R-B", "R-A", "R-C" })
        {
            rafts.Add(await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody(code)));
        }

        await (await owner.Client.PostAsJsonAsync($"/api/rafts/{rafts[2].Id()}/discontinue", new { rowVersion = rafts[2].Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var blocked = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-06", blocked.Text("code"));
        Assert.Equal(["R-A", "R-B"], blocked.GetProperty("raftCodes").EnumerateArray().Select(c => c.GetString()));
        Assert.Contains("2 records are still in use: R-A, R-B", blocked.Text("detail"));

        foreach (var raft in rafts.Take(2))
        {
            await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        }

        // A version the server never issued stands for a stale one (InMemory has no rowversion): nothing changes.
        var stale = await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = "AAAAAAAAB9E=" });
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(StatusIds.Usage.InUse, (await factory.ZoneEntityAsync(zone.Id())).StatusId);

        var discontinued = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { reason = " Season over ", rowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.Discontinued, discontinued.GetProperty("statusId").GetInt32());

        var again = await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = discontinued.Text("rowVersion") });
        Assert.Equal("MSG-GEN-09", await again.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var reactivateInUse = await owner.Client.PostAsJsonAsync($"/api/zones/{(await owner.Client.CreateZoneAsync()).Id()}/reactivate", new { rowVersion = "" });
        Assert.Equal("MSG-GEN-09", await reactivateInUse.ErrorCodeAsync());

        // A Discontinued zone gets no new rafts and leaves the default lookup and map.
        var noRaft = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody())).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-09", "MSG01", "Zone"), (noRaft.Text("code"), noRaft.Text("legacyCode"), noRaft.Text("field")));
        Assert.DoesNotContain(zone.Id(), (await owner.Client.GetFromJsonAsync<JsonElement>("/api/zones/lookup")).EnumerateArray().Select(z => z.Id()));
        Assert.Contains(zone.Id(), (await owner.Client.GetFromJsonAsync<JsonElement>("/api/zones/lookup?includeDiscontinued=true")).EnumerateArray().Select(z => z.Id()));
        Assert.DoesNotContain(zone.Id(), (await owner.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Select(z => z.Id()));

        var reactivated = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/reactivate", new { reason = "New season", rowVersion = discontinued.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.InUse, reactivated.GetProperty("statusId").GetInt32());

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == zone.Id()).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["FarmingZone.Created", "FarmingZone.Discontinued", "FarmingZone.Reactivated"], audits.Select(a => a.Action));
        Assert.All(audits, a => Assert.Equal(zone.Id(), a.ZoneId));
        Assert.All(audits, a => Assert.Equal(zone.Text("code"), a.RecordCode));
        Assert.Equal("Season over", audits[1].Reason);
        Assert.Equal("New season", audits[2].Reason);
        Assert.Equal("IN_USE", JsonNode.Parse(audits[1].Before!)!["Status"]!.GetValue<string>());
        Assert.Equal("OUT_OF_USE", JsonNode.Parse(audits[1].After!)!["Status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_audits_only_the_changed_fields_and_an_unchanged_save_writes_no_audit()
    {
        var owner = await factory.OwnerAsync();
        var body = NewZoneBody();
        var zone = await owner.Client.CreateZoneAsync(body);

        Assert.Equal(["RowVersion"], await (await owner.Client.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Name = "Other" })).InvalidFieldsAsync());

        await (await owner.Client.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Name = "Renamed zone", RowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        // Location, coordinates and the measurement point stay editable (old measurements keep their own point).
        var moved = await (await owner.Client.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with
        {
            Name = "Renamed zone", Location = "Cai Rong", Latitude = 21.1m, Longitude = 107.4m, WaterMeasurementPoint = "Buoy 7", RowVersion = zone.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Buoy 7", moved.Text("waterMeasurementPoint"));
        await (await owner.Client.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with
        {
            Name = "Renamed zone", Location = "Cai Rong", Latitude = 21.1m, Longitude = 107.4m, WaterMeasurementPoint = "Buoy 7", RowVersion = moved.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.OK);

        var updates = await factory.QueryAsync(db => db.AuditEvents
            .Where(e => e.RecordId == zone.Id() && e.Action == "FarmingZone.Updated").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, updates.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"Name":"{{body.Name}}"}"""), JsonNode.Parse(updates[0].Before!)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"Name":"Renamed zone"}"""), JsonNode.Parse(updates[0].After!)));
        Assert.Equal(
            ["Latitude", "Location", "Longitude", "WaterMeasurementPoint"],
            JsonNode.Parse(updates[1].After!)!.AsObject().Select(p => p.Key).Order());
        Assert.Equal("Buoy 3", JsonNode.Parse(updates[1].Before!)!["WaterMeasurementPoint"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_searches_code_or_name_filters_status_pages_and_counts_in_use_rafts()
    {
        var owner = await factory.OwnerAsync();
        var prefix = $"P{NewCode()[..6]}";
        var zones = new Dictionary<string, JsonElement>();
        foreach (var suffix in new[] { "c", "a", "b" })
        {
            zones[suffix] = await owner.Client.CreateZoneAsync(NewZoneBody($"{prefix}-{suffix}"));
        }

        await owner.Client.CreateRaftAsync(zones["a"].Id());
        var stopped = await owner.Client.CreateRaftAsync(zones["a"].Id());
        await (await owner.Client.PostAsJsonAsync($"/api/rafts/{stopped.Id()}/discontinue", new { rowVersion = stopped.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zones["c"].Id()}/discontinue", new { rowVersion = zones["c"].Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var all = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones?search={prefix}");
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal(20, all.GetProperty("pageSize").GetInt32());
        var items = all.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([$"{prefix}-a", $"{prefix}-b", $"{prefix}-c"], items.Select(i => i.Text("code")));
        Assert.Equal(1, items[0].GetProperty("inUseRaftCount").GetInt32());
        Assert.Equal(0, items[1].GetProperty("inUseRaftCount").GetInt32());
        Assert.Equal("Bai Tu Long bay", items[0].Text("location"));
        Assert.Equal(12.5m, items[0].GetProperty("area").GetDecimal());

        var byName = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones?search=Zone {prefix}-b");
        Assert.Equal([$"{prefix}-b"], byName.GetProperty("items").EnumerateArray().Select(i => i.Text("code")));

        var discontinued = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones?search={prefix}&statusId={StatusIds.Usage.Discontinued}");
        Assert.Equal([$"{prefix}-c"], discontinued.GetProperty("items").EnumerateArray().Select(i => i.Text("code")));

        var secondPage = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones?search={prefix}&page=2&pageSize=2");
        Assert.Equal([$"{prefix}-c"], secondPage.GetProperty("items").EnumerateArray().Select(i => i.Text("code")));
        Assert.Equal(3, secondPage.GetProperty("total").GetInt32());

        var capped = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones?pageSize=500");
        Assert.Equal(100, capped.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task License_badge_shows_the_worst_state_today_to_the_owner_only()
    {
        var owner = await factory.OwnerAsync();
        var prefix = $"B{NewCode()[..6]}";
        var missing = await owner.Client.CreateZoneAsync(NewZoneBody($"{prefix}-1"));
        var expired = await owner.Client.CreateZoneAsync(NewZoneBody($"{prefix}-2"));
        var expiring = await owner.Client.CreateZoneAsync(NewZoneBody($"{prefix}-3"));
        var fine = await owner.Client.CreateZoneAsync(NewZoneBody($"{prefix}-4"));

        await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [expired.Id(), expiring.Id(), fine.Id()], DocumentType = "SEA_AREA_ALLOCATION", EffectiveDate = Today.AddYears(-1) });
        await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [expired.Id()], EffectiveDate = Today.AddDays(-400), ExpiryDate = Today.AddDays(-1) });
        await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [expiring.Id()], EffectiveDate = Today.AddDays(-400), ExpiryDate = Today.AddDays(30) });
        await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [fine.Id()], EffectiveDate = Today.AddDays(-400), ExpiryDate = Today.AddDays(31) });

        var rows = (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones?search={prefix}")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Missing", "Expired", "ExpiringSoon", null], rows.Select(r => r.GetProperty("licenseState").GetString()));
        Assert.Equal("Expired", (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{expired.Id()}")).GetProperty("licenseState").GetString());

        // Licenses are Owner-only: staff, even at All zones, never see the badge.
        var staff = await factory.StaffAsync(null);
        var staffRows = (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/zones?search={prefix}")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, staffRows.Count);
        Assert.All(staffRows, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("licenseState").ValueKind));
        Assert.Equal(JsonValueKind.Null, (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{expired.Id()}")).GetProperty("licenseState").ValueKind);
    }

    [Fact]
    public async Task Map_returns_permitted_zones_with_coordinates_and_only_their_in_use_rafts()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync(NewZoneBody() with { Latitude = 20.9m, Longitude = 107.1m });
        var other = await owner.Client.CreateZoneAsync();
        var kept = await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody("M-1") with { Latitude = 20.91m, Longitude = 107.11m });
        var stopped = await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody("M-2"));
        await (await owner.Client.PostAsJsonAsync($"/api/rafts/{stopped.Id()}/discontinue", new { rowVersion = stopped.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var map = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().ToList();
        var entry = map.Single(z => z.Id() == zone.Id());
        Assert.Equal(20.9m, entry.GetProperty("latitude").GetDecimal());
        Assert.Equal(107.1m, entry.GetProperty("longitude").GetDecimal());
        var raft = Assert.Single(entry.GetProperty("rafts").EnumerateArray());
        Assert.Equal(kept.Id(), raft.Id());
        Assert.Equal(20.91m, raft.GetProperty("latitude").GetDecimal());
        Assert.Contains(map, z => z.Id() == other.Id());

        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zone.Id()));
        Assert.Equal([zone.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Select(z => z.Id()));
    }

    [Fact]
    public async Task Staff_read_only_the_zones_of_their_effective_grants_and_traders_read_none()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = await owner.Client.CreateZoneAsync();
        var zoneB = await owner.Client.CreateZoneAsync();
        var entityA = await factory.ZoneEntityAsync(zoneA.Id());

        // Any role counts for reading (here the Accountant, which holds no farming function).
        var staffA = await factory.StaffAsync(entityA, StaffRole.Accountant);
        Assert.Equal(HttpStatusCode.OK, (await staffA.Client.GetAsync($"/api/zones/{zoneA.Id()}")).StatusCode);
        Assert.Equal("MSG-GEN-08", await (await staffA.Client.GetAsync($"/api/zones/{zoneB.Id()}")).ErrorCodeAsync());
        Assert.Equal([zoneA.Id()], (await staffA.Client.GetFromJsonAsync<JsonElement>("/api/zones/lookup")).EnumerateArray().Select(z => z.Id()));
        Assert.Equal([zoneA.Id()], (await staffA.Client.GetFromJsonAsync<JsonElement>("/api/zones")).GetProperty("items").EnumerateArray().Select(z => z.Id()));

        var everywhere = await factory.StaffAsync(null);
        Assert.Equal(HttpStatusCode.OK, (await everywhere.Client.GetAsync($"/api/zones/{zoneB.Id()}")).StatusCode);

        // A pair revoked after login opens nothing any more.
        var revoked = await factory.StaffAsync(entityA);
        await factory.UpdateAsync<AccessGrant>(revoked.Grant!.Id, g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; });
        Assert.Equal("MSG-GEN-08", await (await revoked.Client.GetAsync($"/api/zones/{zoneA.Id()}")).ErrorCodeAsync());
        Assert.Empty((await revoked.Client.GetFromJsonAsync<JsonElement>("/api/zones/lookup")).EnumerateArray());
        Assert.Empty((await revoked.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray());

        var trader = await factory.TraderAsync();
        Assert.Equal("MSG-GEN-08", await (await trader.GetAsync($"/api/zones/{zoneA.Id()}")).ErrorCodeAsync());
        Assert.Empty((await trader.GetFromJsonAsync<JsonElement>("/api/zones")).GetProperty("items").EnumerateArray());
        Assert.Empty((await trader.GetFromJsonAsync<JsonElement>("/api/zones/lookup")).EnumerateArray());
        Assert.Empty((await trader.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray());

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/zones")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/zones", NewZoneBody())).StatusCode);
    }
}
