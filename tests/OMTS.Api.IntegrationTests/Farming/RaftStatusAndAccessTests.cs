using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-04 statuses and access: Discontinued refusals, discontinue and resume, Zone Manager reads and Owner-only writes.</summary>
public class RaftStatusAndAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
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
