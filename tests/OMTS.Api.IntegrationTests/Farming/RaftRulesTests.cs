using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-04 rules of a save: validation, code proposal and unique code per zone, map pin, answer and audit.</summary>
public class RaftRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Create_lists_every_invalid_field_and_saves_nothing()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var url = $"/api/zones/{zoneId}/rafts";

        var fields = await (await owner.Client.PostAsJsonAsync(url, new RaftBody(
            null, 21m, null, new string('l', 501), null, new string('n', 2001), null))).InvalidFieldsAsync();
        Assert.Equal(["Code", "Longitude", "Location", "Size", "Condition notes", "Number of lanes"], fields);

        Assert.Equal(["Number of lanes"], await Invalid(NewRaftBody() with { LaneCount = 0 }));
        Assert.Equal(["Number of lanes"], await Invalid(NewRaftBody() with { LaneCount = -1 }));
        Assert.Equal(["Number of lanes"], await Invalid(NewRaftBody() with { LaneCount = 201 }));
        Assert.Equal(["Code"], await Invalid(NewRaftBody(new string('R', 51))));
        Assert.Equal(["Size"], await Invalid(NewRaftBody() with { Size = new string('s', 101) }));
        Assert.Equal(["Latitude"], await Invalid(NewRaftBody() with { Latitude = 91m }));

        Assert.Empty(await factory.QueryAsync(db => db.Rafts.Where(r => r.ZoneId == zoneId).ToListAsync()));

        async Task<List<string>> Invalid(RaftBody body) => await (await owner.Client.PostAsJsonAsync(url, body)).InvalidFieldsAsync();
    }

    [Fact]
    public async Task Create_answers_the_raft_with_msg_gen_05_lanes_one_to_n_in_use_and_no_photos()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();

        var saved = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody("R-1", lanes: 3))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("Raft saved.", "MSG-GEN-05"), (saved.Text("message"), saved.Text("messageCode")));
        var raft = saved.GetProperty("raft");
        Assert.Equal(StatusIds.Usage.InUse, raft.GetProperty("statusId").GetInt32());
        Assert.Equal(3, raft.GetProperty("laneCount").GetInt32());
        Assert.Equal([1, 2, 3], raft.LaneNumbers(StatusIds.Lane.InUse));
        Assert.All(raft.GetProperty("lanes").EnumerateArray(), l => Assert.Equal("In Use", l.Text("status")));
        Assert.Equal(0, raft.GetProperty("photos").GetArrayLength());
        // No purchase receipt names the raft yet (BR-FARM-05).
        Assert.Equal(0, raft.GetProperty("materialCost").GetDecimal());
        foreach (var gone in new[] { "photoUrl", "layoutVersion", "layoutVersions", "layoutDrawing" })
        {
            Assert.False(raft.TryGetProperty(gone, out _), $"{gone} is no longer part of the raft");
        }

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == raft.Id() && e.Action == "Raft.Created"));
        Assert.Equal(3, JsonNode.Parse(audit.After!)!["LaneCount"]!.GetValue<int>());
        Assert.Equal("R-1", JsonNode.Parse(audit.After!)!["Raft"]!["Code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Map_pin_is_optional_both_or_none_and_a_raft_without_one_stays_off_the_map()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();

        var noPin = await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody("NO-PIN") with { Latitude = null, Longitude = null });
        Assert.Equal(JsonValueKind.Null, noPin.GetProperty("latitude").ValueKind);
        var pinned = await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody("PIN"));

        var one = await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody() with { Longitude = null });
        Assert.Equal(["Longitude"], await one.InvalidFieldsAsync());

        var map = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Single(z => z.Id() == zone.Id());
        Assert.Equal([pinned.Id()], map.GetProperty("rafts").EnumerateArray().Select(r => r.Id()));

        // The pin can be taken off again.
        var cleared = await owner.Client.EditRaftAsync(pinned.Id(), NewRaftBody("PIN") with { Latitude = null, Longitude = null, RowVersion = pinned.Text("rowVersion") });
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("longitude").ValueKind);
    }

    [Fact]
    public async Task Next_code_follows_the_zone_counts_discontinued_rafts_and_may_be_replaced()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync(NewZoneBody($"KV{NewCode()}"));
        var url = $"/api/zones/{zone.Id()}/rafts/next-code";
        var first = (await owner.Client.GetFromJsonAsync<JsonElement>(url)).Text("code");
        Assert.Equal($"{zone.Text("code")}-B01", first);

        var raft = await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody(first));
        Assert.Equal($"{zone.Text("code")}-B02", (await owner.Client.GetFromJsonAsync<JsonElement>(url)).Text("code"));

        // A code of the Owner's own is accepted and does not count; a Discontinued raft keeps its number reserved (GBR-03).
        await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody("Bè số 7"));
        await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody($"{zone.Text("code")}-B05"));
        await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal($"{zone.Text("code")}-B06", (await owner.Client.GetFromJsonAsync<JsonElement>(url)).Text("code"));

        // Numbering is per zone; staff do not get proposals, and an unknown zone is 404.
        var other = await owner.Client.CreateZoneAsync();
        Assert.Equal($"{other.Text("code")}-B01", (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{other.Id()}/rafts/next-code")).Text("code"));
        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zone.Id()));
        Assert.Equal("MSG-GEN-08", await (await staff.Client.GetAsync(url)).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/zones/{Guid.NewGuid()}/rafts/next-code")).StatusCode);
    }

    [Fact]
    public async Task Code_is_unique_within_the_zone_ignoring_case()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = (await owner.Client.CreateZoneAsync()).Id();
        var zoneB = (await owner.Client.CreateZoneAsync()).Id();
        var first = await owner.Client.CreateRaftAsync(zoneA, NewRaftBody(" Raft-01 "));
        Assert.Equal("Raft-01", first.Text("code"));

        var duplicate = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zoneA}/rafts", NewRaftBody("raft-01"))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-02", duplicate.Text("code"));
        Assert.Equal(first.Id(), duplicate.GetProperty("existingId").GetGuid());
        Assert.Equal(first.Id(), duplicate.GetProperty("existingRaftId").GetGuid());

        // The same code is fine in another zone.
        await owner.Client.CreateRaftAsync(zoneB, NewRaftBody("RAFT-01"));

        var second = await owner.Client.CreateRaftAsync(zoneA, NewRaftBody("Raft-02"));
        var renamed = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{second.Id()}", NewRaftBody("RAFT-01") with { RowVersion = second.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(first.Id(), renamed.GetProperty("existingRaftId").GetGuid());
    }

    [Fact]
    public async Task Edit_answers_msg_gen_05_and_audits_only_the_fields_that_changed()
    {
        var owner = await factory.OwnerAsync();
        var body = NewRaftBody();
        var raft = await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id(), body);

        var saved = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Size = "50 m x 20 m", ConditionNotes = "Two floats replaced", RowVersion = raft.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Raft saved.", "MSG-GEN-05"), (saved.Text("message"), saved.Text("messageCode")));
        Assert.Equal("Two floats replaced", saved.GetProperty("raft").Text("conditionNotes"));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == raft.Id() && e.Action == "Raft.Updated"));
        Assert.Equal(["ConditionNotes", "LengthM", "Size"], JsonNode.Parse(audit.After!)!.AsObject().Select(p => p.Key).Order());
        Assert.Equal("40 m x 20 m", JsonNode.Parse(audit.Before!)!["Size"]!.GetValue<string>());
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raft.Id() && e.Action == "Raft.LanesChanged")));
    }
}
