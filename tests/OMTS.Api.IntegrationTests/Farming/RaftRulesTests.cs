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

/// <summary>FARM-04 rules: validation, code proposal and unique code per zone, map pin, Discontinued refusals, statuses and access.</summary>
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
        Assert.Equal(["ConditionNotes", "Size"], JsonNode.Parse(audit.After!)!.AsObject().Select(p => p.Key).Order());
        Assert.Equal("40 m x 20 m", JsonNode.Parse(audit.Before!)!["Size"]!.GetValue<string>());
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raft.Id() && e.Action == "Raft.LanesChanged")));
    }

    // GBR-01, FARM-04.2.E2: a Discontinued raft is read-only until it is resumed; the checks come before the fields.
    [Fact]
    public async Task Every_edit_of_a_discontinued_raft_is_refused_with_msg_gen_09_until_it_is_resumed()
    {
        var owner = await factory.OwnerAsync();
        var raft = await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id(), NewRaftBody("R-STOP"));
        var photo = (await owner.Client.AddRaftPhotoAsync(raft.Id())).GetProperty("photos")[0].Id();
        var current = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        var stopped = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { reason = "Storm", rowVersion = current.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var expected = "Raft R-STOP is Discontinued and cannot be used for this action.";

        // An invalid body is still answered by the status.
        var edit = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", new RaftBody(null, null, null, null, null, null, null))).ReadAsync(HttpStatusCode.Conflict);
        var upload = await (await owner.Client.UploadRaftPhotoAsync(raft.Id())).ReadAsync(HttpStatusCode.Conflict);
        var remove = await (await owner.Client.DeleteAsync($"/api/rafts/{raft.Id()}/photos/{photo}")).ReadAsync(HttpStatusCode.Conflict);
        foreach (var refused in new[] { edit, upload, remove })
        {
            Assert.Equal(("MSG-GEN-09", expected), (refused.Text("code"), refused.Text("detail")));
        }

        Assert.Single(await factory.RaftPhotoKeysAsync(raft.Id()));

        // Reads stay open, and Resume gives it back with the same code, lanes and photo.
        Assert.Equal("Discontinued", (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}")).Text("status"));
        var back = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/resume", new { rowVersion = stopped.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.InUse, back.GetProperty("statusId").GetInt32());
        Assert.Equal(1, back.GetProperty("photos").GetArrayLength());
        Assert.Equal(5, back.GetProperty("laneCount").GetInt32());
        await owner.Client.EditRaftAsync(raft.Id(), NewRaftBody("R-STOP") with { ConditionNotes = "Repaired", RowVersion = back.Text("rowVersion") });
    }

    // GBR-01: "while a zone is discontinued its rafts cannot be changed". Only an older database has such a raft In Use.
    [Fact]
    public async Task A_raft_in_use_in_a_discontinued_zone_is_refused_naming_the_zone()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var raft = await owner.Client.CreateRaftAsync(zone.Id());
        await factory.UpdateAsync<FarmingZone>(zone.Id(), z => z.StatusId = StatusIds.Usage.Discontinued);

        var refused = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code")) with { RowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-GEN-09", $"Farming zone {zone.Text("code")} is Discontinued and cannot be used for this action."), (refused.Text("code"), refused.Text("detail")));
    }

    [Fact]
    public async Task Discontinue_and_resume_follow_the_status_rules()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var raft = await owner.Client.CreateRaftAsync(zone.Id());

        // Stale versions (never issued, as InMemory has no rowversion) are refused for edits and transitions.
        Assert.Equal("MSG06", await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code")) with { RowVersion = StaleRowVersion })).ErrorCodeAsync());
        Assert.Equal("MSG06", await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = StaleRowVersion })).ErrorCodeAsync());

        var discontinued = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { reason = "Storm damage", rowVersion = raft.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.Discontinued, discontinued.GetProperty("statusId").GetInt32());
        Assert.Equal("MSG-GEN-09", await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = discontinued.Text("rowVersion") })).ErrorCodeAsync());

        // The zone is free now; a raft of a Discontinued zone cannot come back.
        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var inStoppedZone = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/resume", new { rowVersion = discontinued.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", inStoppedZone.Text("code"));
        Assert.Equal($"Farming zone {zone.Text("code")} is Discontinued and cannot be used for this action.", inStoppedZone.Text("detail"));

        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        // The older route of Resume still works for one release.
        var back = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/reactivate", new { reason = "Repaired", rowVersion = discontinued.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.InUse, back.GetProperty("statusId").GetInt32());
        Assert.Equal("MSG-GEN-09", await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/resume", new { rowVersion = back.Text("rowVersion") })).ErrorCodeAsync());

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raft.Id()).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["Raft.Created", "Raft.Discontinued", "Raft.Reactivated"], audits.Select(a => a.Action));
        Assert.Equal(["Storm damage", "Repaired"], audits.Skip(1).Select(a => a.Reason));
        Assert.All(audits, a => Assert.Equal(zone.Id(), a.ZoneId));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/rafts/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PostAsJsonAsync($"/api/rafts/{Guid.NewGuid()}/discontinue", new { rowVersion = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/rafts/{Guid.NewGuid()}/lanes/today")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/rafts/{raft.Id()}/condition")).StatusCode);
    }

    // Q27: FARM-04 is Owner-only, but a Zone Manager keeps reading the rafts of own zones (CYCLE, HARVEST and WATER screens).
    [Fact]
    public async Task Zone_manager_reads_the_rafts_of_own_zones_only_and_never_writes()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = await owner.Client.CreateZoneAsync();
        var zoneB = await owner.Client.CreateZoneAsync();
        var raftA = await owner.Client.CreateRaftAsync(zoneA.Id());
        var raftB = await owner.Client.CreateRaftAsync(zoneB.Id());
        var withPhoto = await owner.Client.AddRaftPhotoAsync(raftA.Id());
        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneA.Id()));

        Assert.Equal([raftA.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneA.Id()}/rafts")).EnumerateArray().Select(r => r.Id()));
        var detail = await staff.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftA.Id()}");
        Assert.Equal(0, detail.GetProperty("materialCost").GetDecimal());
        Assert.Equal(5, detail.GetProperty("lanes").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await staff.Client.GetAsync(detail.GetProperty("photos")[0].Text("url"))).StatusCode);
        Assert.Equal(5, (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftA.Id()}/lanes/today")).GetProperty("lanes").GetArrayLength());

        Assert.Equal("MSG-GEN-08", await (await staff.Client.GetAsync($"/api/zones/{zoneB.Id()}/rafts")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await staff.Client.GetAsync($"/api/rafts/{raftB.Id()}")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await staff.Client.GetAsync($"/api/rafts/{raftB.Id()}/lanes/today")).ErrorCodeAsync());

        // Owner-only writes: even a role holding every function in the zone gets MSG-GEN-08.
        var trader = await factory.TraderAsync();
        var photoId = withPhoto.GetProperty("photos")[0].Id();
        foreach (var client in new[] { staff.Client, trader })
        {
            Assert.Equal("MSG-GEN-08", await (await client.PostAsJsonAsync($"/api/zones/{zoneA.Id()}/rafts", NewRaftBody())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PutAsJsonAsync($"/api/rafts/{raftA.Id()}", NewRaftBody() with { RowVersion = raftA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.UploadRaftPhotoAsync(raftA.Id())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.DeleteAsync($"/api/rafts/{raftA.Id()}/photos/{photoId}")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsJsonAsync($"/api/rafts/{raftA.Id()}/discontinue", new { rowVersion = raftA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsJsonAsync($"/api/rafts/{raftA.Id()}/resume", new { rowVersion = raftA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsJsonAsync("/api/zones", NewZoneBody())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PutAsJsonAsync($"/api/zones/{zoneA.Id()}", NewZoneBody() with { RowVersion = zoneA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsJsonAsync($"/api/zones/{zoneA.Id()}/discontinue", new { rowVersion = zoneA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsJsonAsync($"/api/zones/{zoneA.Id()}/reactivate", new { rowVersion = zoneA.Text("rowVersion") })).ErrorCodeAsync());
        }

        Assert.Equal("MSG-GEN-08", await (await trader.GetAsync($"/api/zones/{zoneA.Id()}/rafts")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await trader.GetAsync($"/api/rafts/{raftA.Id()}")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await trader.GetAsync($"/api/rafts/{raftA.Id()}/lanes/today")).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/rafts/{raftA.Id()}")).StatusCode);

        var unchanged = await factory.QueryAsync(db => db.Rafts.SingleAsync(r => r.Id == raftA.Id()));
        Assert.Equal(StatusIds.Usage.InUse, unchanged.StatusId);
        Assert.Single(await factory.RaftPhotoKeysAsync(raftA.Id()));
    }
}
