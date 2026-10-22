using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-04 rules and error bullets: validation, unique code per zone, layout versions, lane removal, statuses, photo and access.</summary>
public class RaftRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Create_lists_every_invalid_field_and_saves_nothing()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var url = $"/api/zones/{zoneId}/rafts";

        var fields = await (await owner.Client.PostAsJsonAsync(url, new RaftBody(
            null, 21m, null, new string('l', 501), null, new string('n', 2001), []))).InvalidFieldsAsync();
        Assert.Equal(["Code", "Longitude", "Location", "Size", "Condition notes", "Lanes"], fields);

        Assert.Equal(["Lanes"], await Invalid(NewRaftBody() with { Lanes = null }));
        Assert.Equal(["Lanes"], await Invalid(NewRaftBody() with { Lanes = [new(1, 10, "A", "B"), new(1, 10, "A", "B")] }));
        Assert.Equal(["Lanes"], await Invalid(NewRaftBody() with { Lanes = [new(2, 10, "A", "B"), new(3, 10, "A", "B")] }));
        Assert.Equal(
            ["Lane 1 length", "Lane 2 length", "Lane 2 marker A", "Lane 3 length", "Lane 3 marker B"],
            await Invalid(NewRaftBody() with { Lanes = [new(1, 0, "A", "B"), new(2, -5, " ", "B"), new(3, 1.234m, "A", new string('b', 51))] }));
        Assert.Equal(["Lane 1 marker B"], await Invalid(NewRaftBody() with { Lanes = [new(1, 10, "A", null)] }));
        Assert.Equal(["Lane 1 length"], await Invalid(NewRaftBody() with { Lanes = [new(1, null, "A", "B")] }));
        Assert.Equal(["Code"], await Invalid(NewRaftBody(new string('R', 51))));
        Assert.Equal(["Size"], await Invalid(NewRaftBody() with { Size = new string('s', 101) }));
        Assert.Equal(["Layout drawing"], await Invalid(NewRaftBody() with { LayoutDrawing = new { data = new string('d', 51 * 1024) } }));

        Assert.Empty(await factory.QueryAsync(db => db.Rafts.Where(r => r.ZoneId == zoneId).ToListAsync()));

        async Task<List<string>> Invalid(RaftBody body) => await (await owner.Client.PostAsJsonAsync(url, body)).InvalidFieldsAsync();
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
        Assert.Equal("MSG03", duplicate.Text("code"));
        Assert.Equal(first.Id(), duplicate.GetProperty("existingRaftId").GetGuid());

        // The same code is fine in another zone.
        await owner.Client.CreateRaftAsync(zoneB, NewRaftBody("RAFT-01"));

        var second = await owner.Client.CreateRaftAsync(zoneA, NewRaftBody("Raft-02"));
        var renamed = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{second.Id()}", NewRaftBody("RAFT-01") with { RowVersion = second.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(first.Id(), renamed.GetProperty("existingRaftId").GetGuid());
    }

    [Fact]
    public async Task Info_edit_and_drawing_change_keep_the_layout_version()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var body = NewRaftBody();
        var raft = await owner.Client.CreateRaftAsync(zoneId, body);

        var edited = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Size = "50 m x 20 m", ConditionNotes = "Two floats replaced", RowVersion = raft.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, edited.GetProperty("layoutVersion").GetInt32());
        Assert.Equal(1, edited.GetProperty("layoutVersions").GetArrayLength());
        Assert.Equal("Two floats replaced", edited.Text("conditionNotes"));

        var drawn = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Size = "50 m x 20 m", ConditionNotes = "Two floats replaced", LayoutDrawing = new { shapes = new[] { "line" } }, RowVersion = edited.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, drawn.GetProperty("layoutVersion").GetInt32());
        Assert.Equal("line", drawn.GetProperty("layoutDrawing").GetProperty("shapes")[0].GetString());
        Assert.Equal(1, await factory.QueryAsync(db => db.RaftLayoutVersions.CountAsync(v => v.RaftId == raft.Id())));

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raft.Id()).OrderBy(e => e.OccurredAt).ToListAsync());
        // A drawing-only change is audited but creates no layout version (rows of one save share a timestamp, so compare as a set).
        Assert.Equal(["Raft.Created", "Raft.LayoutDrawingChanged", "Raft.Updated"], audits.Select(a => a.Action).Order());
        Assert.Equal(["ConditionNotes", "Size"], JsonNode.Parse(audits[1].After!)!.AsObject().Select(p => p.Key).Order());
        Assert.Equal("40 m x 20 m", JsonNode.Parse(audits[1].Before!)!["Size"]!.GetValue<string>());
    }

    [Fact]
    public async Task Layout_change_chains_versions_and_the_condition_view_reads_the_old_version_on_an_old_date()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var body = NewRaftBody(lanes: 3);
        var raft = await owner.Client.CreateRaftAsync(zoneId, body);

        var relaid = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Lanes = [new(1, 40, "A", "B"), new(2, 35, "A", "B"), new(3, 40, "A2", "B")], RowVersion = raft.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, relaid.GetProperty("layoutVersion").GetInt32());
        Assert.Equal(35m, relaid.GetProperty("lanes")[1].GetProperty("length").GetDecimal());
        Assert.Equal("A2", relaid.GetProperty("lanes")[2].Text("markerA"));
        // Lane ids are kept when lanes are only changed.
        Assert.Equal(raft.LaneId(2), relaid.LaneId(2));

        var third = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Lanes = [new(1, 40, "A", "B"), new(2, 35, "A", "B"), new(3, 40, "A2", "B"), new(4, 20, "A", "B")], RowVersion = relaid.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.OK);
        var versions = third.GetProperty("layoutVersions").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], versions.Select(v => v.GetProperty("versionNumber").GetInt32()));
        Assert.Equal([3, 3, 4], versions.Select(v => v.GetProperty("laneCount").GetInt32()));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), versions[0].Text("validTo"));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), versions[1].Text("validTo"));
        Assert.Equal(JsonValueKind.Null, versions[2].GetProperty("validTo").ValueKind);
        Assert.Equal(Today.ToString("yyyy-MM-dd"), versions[2].Text("validFrom"));

        var changes = await factory.QueryAsync(db => db.AuditEvents
            .Where(e => e.RecordId == raft.Id() && e.Action == "Raft.LayoutChanged").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal([1, 2], changes.Select(c => JsonNode.Parse(c.Before!)!["Version"]!.GetValue<int>()));
        Assert.Equal([2, 3], changes.Select(c => JsonNode.Parse(c.After!)!["Version"]!.GetValue<int>()));

        // Version 1 keeps its snapshot; seen from a date before the changes the condition view uses it.
        var first = await factory.QueryAsync(db => db.RaftLayoutVersions.SingleAsync(v => v.RaftId == raft.Id() && v.VersionNumber == 1));
        Assert.Equal(40m, JsonNode.Parse(first.LayoutMap!)!["lanes"]![1]!["length"]!.GetValue<decimal>());
        await factory.UpdateAsync<OMTS.Domain.Entities.Farming.RaftLayoutVersion>(first.Id, v => v.ValidFrom = Today.AddDays(-10));

        var past = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition?date={Today.AddDays(-5):yyyy-MM-dd}");
        Assert.Equal(1, past.GetProperty("layoutVersion").GetInt32());
        Assert.Equal([40m, 40m, 40m], past.GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("length").GetDecimal()));
        var now = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition");
        Assert.Equal(3, now.GetProperty("layoutVersion").GetInt32());
        Assert.Equal([40m, 35m, 40m, 20m], now.GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("length").GetDecimal()));
    }

    [Fact]
    public async Task Lane_with_segments_cannot_be_removed_and_a_lane_without_segments_can()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var body = NewRaftBody(lanes: 5);
        var raft = await owner.Client.CreateRaftAsync(zoneId, body);

        // A closed cycle (no open cycle blocks the change) left a segment on lane 3.
        var cycle = await factory.AddCycleAsync(raft.Id(), DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(-30));
        await factory.AddSegmentAsync(cycle.Id, raft.LaneId(3), LaneSegmentType.Seeded, 0, 10, Today.AddDays(-50), owner.Account.Id);

        var shorter = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code"), lanes: 4) with { RowVersion = raft.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(4, shorter.GetProperty("lanes").GetArrayLength());
        Assert.Equal(4, await factory.QueryAsync(db => db.Lanes.CountAsync(l => l.RaftId == raft.Id())));

        var blocked = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code"), lanes: 2) with { RowVersion = shorter.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG11", blocked.Text("code"));
        Assert.Equal([3], blocked.GetProperty("laneNumbers").EnumerateArray().Select(n => n.GetInt32()));
        Assert.Contains("lane 3", blocked.Text("detail"));

        var unchanged = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.Equal(4, unchanged.GetProperty("lanes").GetArrayLength());
        Assert.Equal(2, unchanged.GetProperty("layoutVersion").GetInt32());
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raft.Id() && e.Action == "Raft.LayoutChanged")));
    }

    [Fact]
    public async Task Open_cycle_blocks_layout_changes_and_discontinue_but_not_info_edits()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var body = NewRaftBody(lanes: 2);
        var raft = await owner.Client.CreateRaftAsync(zoneId, body);
        var cycle = await factory.AddCycleAsync(raft.Id(), DateTimeOffset.UtcNow.AddDays(-3));

        var lengths = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Lanes = [new(1, 40, "A", "B"), new(2, 30, "A", "B")], RowVersion = raft.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG11", lengths.Text("code"));
        Assert.Equal(cycle.Id, lengths.GetProperty("cycleId").GetGuid());
        var markers = await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Lanes = [new(1, 40, "A", "B"), new(2, 40, "A", "Z")], RowVersion = raft.Text("rowVersion"),
        });
        Assert.Equal("MSG11", await markers.ErrorCodeAsync());

        var notes = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { ConditionNotes = "Rope worn", RowVersion = raft.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, notes.GetProperty("layoutVersion").GetInt32());

        var discontinue = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = notes.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG61", discontinue.Text("code"));
        Assert.Contains("open cultivation cycle", discontinue.Text("detail"));
    }

    [Fact]
    public async Task Discontinue_and_reactivate_follow_the_status_rules()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var raft = await owner.Client.CreateRaftAsync(zone.Id());

        // Stale versions (never issued, as InMemory has no rowversion) are refused for edits and transitions.
        Assert.Equal("MSG06", await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code")) with { RowVersion = "AAAAAAAAB9E=" })).ErrorCodeAsync());
        Assert.Equal("MSG06", await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = "AAAAAAAAB9E=" })).ErrorCodeAsync());

        var discontinued = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { reason = "Storm damage", rowVersion = raft.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.Discontinued, discontinued.GetProperty("statusId").GetInt32());
        Assert.Equal("MSG61", await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = discontinued.Text("rowVersion") })).ErrorCodeAsync());

        // The zone is free now; a raft of a Discontinued zone cannot come back.
        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var inStoppedZone = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/reactivate", new { rowVersion = discontinued.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG61", inStoppedZone.Text("code"));
        Assert.Contains("zone is discontinued", inStoppedZone.Text("detail"));

        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/reactivate", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var back = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/reactivate", new { reason = "Repaired", rowVersion = discontinued.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.InUse, back.GetProperty("statusId").GetInt32());
        Assert.Equal("MSG61", await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/reactivate", new { rowVersion = back.Text("rowVersion") })).ErrorCodeAsync());

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raft.Id()).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["Raft.Created", "Raft.Discontinued", "Raft.Reactivated"], audits.Select(a => a.Action));
        Assert.Equal(["Storm damage", "Repaired"], audits.Skip(1).Select(a => a.Reason));
        Assert.All(audits, a => Assert.Equal(zone.Id(), a.ZoneId));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/rafts/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PostAsJsonAsync($"/api/rafts/{Guid.NewGuid()}/discontinue", new { rowVersion = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/rafts/{Guid.NewGuid()}/condition")).StatusCode);
    }

    [Fact]
    public async Task Photo_must_really_be_jpeg_or_png_of_at_most_10_MB()
    {
        var owner = await factory.OwnerAsync();
        var raft = await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id());
        var url = $"/api/rafts/{raft.Id()}/photo";
        var before = factory.StoredFiles("raft-photos").Count;

        Assert.Equal(["Photo"], await (await Upload(TextPretendingToBeAFile, "raft.png")).InvalidFieldsAsync());
        Assert.Equal(["Photo"], await (await Upload(Pdf, "raft.jpg")).InvalidFieldsAsync());
        Assert.Equal(["Photo"], await (await Upload(WebP, "raft.webp")).InvalidFieldsAsync());
        Assert.Equal(["Photo"], await (await Upload(Sized(Png, 10 * 1024 * 1024 + 1), "big.png")).InvalidFieldsAsync());
        Assert.Equal(["Photo"], await (await owner.Client.PostAsync(url, new MultipartFormDataContent { { new StringContent("x"), "note" } })).InvalidFieldsAsync());
        Assert.Equal(before, factory.StoredFiles("raft-photos").Count);
        Assert.Null((await factory.QueryAsync(db => db.Rafts.SingleAsync(r => r.Id == raft.Id()))).PhotoUrl);

        var withPhoto = await (await Upload(Sized(Png, 10 * 1024 * 1024), "exactly-10-mb.png")).ReadAsync(HttpStatusCode.OK);
        Assert.NotNull(withPhoto.GetProperty("photoUrl").GetString());
        var jpeg = await (await Upload(Jpeg, "raft.jpeg")).ReadAsync(HttpStatusCode.OK);
        Assert.NotEqual(withPhoto.Text("photoUrl"), jpeg.Text("photoUrl"));
        Assert.Equal(before + 2, factory.StoredFiles("raft-photos").Count);

        var photoAudits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raft.Id() && e.Action == "Raft.Updated").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, photoAudits.Count);
        Assert.Equal(JsonNode.Parse(photoAudits[0].After!)!["PhotoUrl"]!.GetValue<string>(), JsonNode.Parse(photoAudits[1].Before!)!["PhotoUrl"]!.GetValue<string>());

        Task<HttpResponseMessage> Upload(byte[] bytes, string name) =>
            owner.Client.PostAsync(url, new MultipartFormDataContent { { FileContent(bytes, "image/png"), "file", name } });
    }

    [Fact]
    public async Task Staff_of_one_zone_never_read_the_rafts_of_another_and_never_write()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = await owner.Client.CreateZoneAsync();
        var zoneB = await owner.Client.CreateZoneAsync();
        var raftA = await owner.Client.CreateRaftAsync(zoneA.Id());
        var raftB = await owner.Client.CreateRaftAsync(zoneB.Id());
        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneA.Id()), EveryFunction());

        Assert.Equal([raftA.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneA.Id()}/rafts")).EnumerateArray().Select(r => r.Id()));
        var detail = await staff.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftA.Id()}");
        // Purchase receipts exist since OMTS-209: a raft with none costs 0.
        Assert.Equal(0, detail.GetProperty("materialCost").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, (await staff.Client.GetAsync($"/api/rafts/{raftA.Id()}/condition")).StatusCode);

        Assert.Equal("MSG02", await (await staff.Client.GetAsync($"/api/zones/{zoneB.Id()}/rafts")).ErrorCodeAsync());
        Assert.Equal("MSG02", await (await staff.Client.GetAsync($"/api/rafts/{raftB.Id()}")).ErrorCodeAsync());
        Assert.Equal("MSG02", await (await staff.Client.GetAsync($"/api/rafts/{raftB.Id()}/condition")).ErrorCodeAsync());

        // Owner-only writes: even a role holding every function in the zone gets MSG02.
        var trader = await factory.TraderAsync();
        foreach (var client in new[] { staff.Client, trader })
        {
            Assert.Equal("MSG02", await (await client.PostAsJsonAsync($"/api/zones/{zoneA.Id()}/rafts", NewRaftBody())).ErrorCodeAsync());
            Assert.Equal("MSG02", await (await client.PutAsJsonAsync($"/api/rafts/{raftA.Id()}", NewRaftBody() with { RowVersion = raftA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG02", await (await client.PostAsync($"/api/rafts/{raftA.Id()}/photo", new MultipartFormDataContent { { FileContent(Png), "file", "a.png" } })).ErrorCodeAsync());
            Assert.Equal("MSG02", await (await client.PostAsJsonAsync($"/api/rafts/{raftA.Id()}/discontinue", new { rowVersion = raftA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG02", await (await client.PostAsJsonAsync($"/api/rafts/{raftA.Id()}/reactivate", new { rowVersion = raftA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG02", await (await client.PostAsJsonAsync("/api/zones", NewZoneBody())).ErrorCodeAsync());
            Assert.Equal("MSG02", await (await client.PutAsJsonAsync($"/api/zones/{zoneA.Id()}", NewZoneBody() with { RowVersion = zoneA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG02", await (await client.PostAsJsonAsync($"/api/zones/{zoneA.Id()}/discontinue", new { rowVersion = zoneA.Text("rowVersion") })).ErrorCodeAsync());
            Assert.Equal("MSG02", await (await client.PostAsJsonAsync($"/api/zones/{zoneA.Id()}/reactivate", new { rowVersion = zoneA.Text("rowVersion") })).ErrorCodeAsync());
        }

        Assert.Equal("MSG02", await (await trader.GetAsync($"/api/zones/{zoneA.Id()}/rafts")).ErrorCodeAsync());
        Assert.Equal("MSG02", await (await trader.GetAsync($"/api/rafts/{raftA.Id()}")).ErrorCodeAsync());
        Assert.Equal("MSG02", await (await trader.GetAsync($"/api/rafts/{raftA.Id()}/condition")).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/rafts/{raftA.Id()}")).StatusCode);

        var unchanged = await factory.QueryAsync(db => db.Rafts.SingleAsync(r => r.Id == raftA.Id()));
        Assert.Equal(StatusIds.Usage.InUse, unchanged.StatusId);
        Assert.Null(unchanged.PhotoUrl);
    }
}
