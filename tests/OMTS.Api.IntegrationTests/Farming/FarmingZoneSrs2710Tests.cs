using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// FARM-02 of the SRS 27/10 (PR 0.8): the proposed zone code (GBR-03), a Discontinued zone is read-only (GBR-01,
/// MSG-GEN-09), Resume (FARM-02.3) and the optional water measurement point (BR-WATER-01).
/// </summary>
public class FarmingZoneSrs2710Tests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // GBR-03: KV01, KV02…; the highest KVnn in use plus one, discontinued zones included, case ignored; other codes are ignored.
    [Fact]
    public async Task Next_code_proposes_the_next_kv_number_and_is_owner_only()
    {
        using var api = new AuthApiFactory();
        var owner = await api.OwnerAsync();

        Assert.Equal("KV01", await NextCodeAsync(owner.Client));
        await owner.Client.CreateZoneAsync(NewZoneBody("KV01"));
        Assert.Equal("KV02", await NextCodeAsync(owner.Client));

        // A discontinued zone keeps its code reserved; lower case and a number of one digit count too.
        var old = await owner.Client.CreateZoneAsync(NewZoneBody("kv7"));
        await (await owner.Client.PostAsJsonAsync($"/api/zones/{old.Id()}/discontinue", new { rowVersion = old.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await owner.Client.CreateZoneAsync(NewZoneBody("KV-NORTH"));
        await owner.Client.CreateZoneAsync(NewZoneBody("ZONE99"));
        Assert.Equal("KV08", await NextCodeAsync(owner.Client));
        await owner.Client.CreateZoneAsync(NewZoneBody("KV120"));
        Assert.Equal("KV121", await NextCodeAsync(owner.Client));

        // The proposal is only a proposal: the Owner may save another code.
        Assert.Equal("Bai-1", (await owner.Client.CreateZoneAsync(NewZoneBody("Bai-1"))).Text("code"));

        var staff = await api.StaffAsync(null);
        Assert.Equal("MSG-GEN-08", await (await staff.Client.GetAsync("/api/zones/next-code")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await (await api.TraderAsync()).GetAsync("/api/zones/next-code")).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/zones/next-code")).StatusCode);
    }

    // FARM-02.1.E1: the zone is checked before anything else, and nothing is saved or audited.
    [Fact]
    public async Task Editing_a_discontinued_zone_is_refused_with_msg_gen_09_until_it_is_resumed()
    {
        var owner = await factory.OwnerAsync();
        var body = NewZoneBody();
        var zone = await owner.Client.CreateZoneAsync(body);
        var discontinued = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);

        foreach (var edit in new[] { body with { Name = "Renamed", RowVersion = discontinued.Text("rowVersion") }, body with { Name = " ", RowVersion = null } })
        {
            var problem = await (await owner.Client.PutAsJsonAsync($"/api/zones/{zone.Id()}", edit)).ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(("MSG-GEN-09", "MSG61"), (problem.Text("code"), problem.Text("legacyCode")));
            Assert.Equal($"Farming zone {body.Code} is Discontinued and cannot be used for this action.", problem.Text("detail"));
        }

        Assert.Equal(body.Name, (await factory.ZoneEntityAsync(zone.Id())).Name);
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == zone.Id() && e.Action == "FarmingZone.Updated")));

        var resumed = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { rowVersion = discontinued.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        var edited = await owner.Client.EditZoneAsync(zone.Id(), body with { Name = "Renamed", RowVersion = resumed.Text("rowVersion") });
        Assert.Equal("Renamed", edited.Text("name"));
    }

    // FARM-02.3: same code and history; /reactivate stays for one release; resuming an In Use zone answers MSG-GEN-09.
    [Fact]
    public async Task Resume_returns_the_zone_to_in_use_and_the_older_route_still_works()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var raft = await owner.Client.CreateRaftAsync(zone.Id());
        await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var discontinued = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);

        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zone.Id()));
        Assert.Equal("MSG-GEN-08", await (await staff.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { rowVersion = discontinued.Text("rowVersion") })).ErrorCodeAsync());

        var resumed = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { reason = "New season", rowVersion = discontinued.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal((StatusIds.Usage.InUse, zone.Text("code")), (resumed.GetProperty("statusId").GetInt32(), resumed.Text("code")));
        Assert.Equal("MSG-GEN-09", await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { rowVersion = resumed.Text("rowVersion") })).ErrorCodeAsync());
        // Its rafts keep their own status (they are resumed one by one in FARM-04).
        Assert.Equal(StatusIds.Usage.Discontinued, (await factory.QueryAsync(db => db.Rafts.SingleAsync(r => r.Id == raft.Id()))).StatusId);

        var again = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = resumed.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/reactivate", new { rowVersion = again.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == zone.Id()).OrderBy(e => e.OccurredAt).Select(e => new { e.Action, e.Reason }).ToListAsync());
        Assert.Equal(
            ["FarmingZone.Created", "FarmingZone.Discontinued", "FarmingZone.Reactivated", "FarmingZone.Discontinued", "FarmingZone.Reactivated"],
            audits.Select(a => a.Action));
        Assert.Equal("New season", audits[2].Reason);
    }

    // BR-WATER-01: water is measured per zone with no fixed point, so the point is optional on create and edit.
    private static async Task<string> NextCodeAsync(HttpClient owner) =>
        (await (await owner.GetAsync("/api/zones/next-code")).ReadAsync(HttpStatusCode.OK)).Text("code");
}
