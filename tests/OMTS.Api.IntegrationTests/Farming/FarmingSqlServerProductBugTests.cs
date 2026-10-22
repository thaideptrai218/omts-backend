using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// PENDING (product bugs found in the OMTS-210 QA review; not compiled until fixed, rename to .cs to run):
/// a lane layout or zone list change does not move the record's rowversion, markers in Vietnamese overflow
/// their column, and SQL Server decimals are audited as changed when they are not.
/// </summary>
public class FarmingSqlServerProductBugTests
{
    // FARM-04 "Stale rowVersion → 409 MSG06": only Lane/RaftLayoutVersion rows change, so the Raft rowversion stays
    // the same and a second editor holding the old version silently replaces the first editor's layout.
    [SqlServerFact]
    public async Task Layout_change_with_a_stale_row_version_is_refused()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody(lanes: 3);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);

        await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Lanes = [new(1, 40, "A", "B"), new(2, 35, "A", "B"), new(3, 40, "A", "B")], RowVersion = raft.Text("rowVersion"),
        })).ReadAsync(HttpStatusCode.OK);

        var stale = await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with
        {
            Lanes = [new(1, 40, "A", "B"), new(2, 40, "A", "B"), new(3, 40, "A", "B"), new(4, 40, "A", "B")], RowVersion = raft.Text("rowVersion"),
        });

        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(2, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}")).GetProperty("layoutVersion").GetInt32());
    }

    // FARM-03 edit: changing only the covered zones changes DocumentZoneScope rows, not the license row.
    [SqlServerFact]
    public async Task License_edit_after_a_zone_only_edit_with_the_old_row_version_is_refused()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneA = (await owner.CreateZoneAsync()).Id();
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var fields = new LicenseFields { ZoneIds = [zoneA], EffectiveDate = Today };
        var license = await owner.CreateLicenseAsync(fields);

        await (await owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneIds = [zoneA, zoneB], File = null, RowVersion = license.Text("rowVersion") }).ToForm()))
            .ReadAsync(HttpStatusCode.OK);
        var stale = await owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneIds = [zoneA], Holder = "Late", File = null, RowVersion = license.Text("rowVersion") }).ToForm());

        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
    }

    // FARM-04 markers ≤ 50 characters each: the JSON stored in Lanes.MeasurementMarkers (nvarchar(500)) escapes every
    // non-ASCII character as \uXXXX (6 characters), so two 50-character Vietnamese markers need 627 characters → 500.
    [SqlServerFact]
    public async Task Markers_of_50_vietnamese_characters_are_saved()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var marker = new string('ố', 50);

        var response = await owner.PostAsJsonAsync($"/api/zones/{(await owner.CreateZoneAsync()).Id()}/rafts", NewRaftBody() with { Lanes = [new(1, 40, marker, marker)] });

        var raft = await response.ReadAsync(HttpStatusCode.Created);
        Assert.Equal(marker, raft.GetProperty("lanes")[0].Text("markerA"));
    }

    // FARM-02/04 "Updated (only changed fields in before/after)": SQL Server returns decimal(9,6) as 21.050000 while the
    // request holds 21.05, so the JSON diff reports unchanged coordinates (and area) as changed on every edit.
    [SqlServerFact]
    public async Task Edits_audit_only_the_fields_that_really_changed()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewZoneBody();
        var zone = await owner.CreateZoneAsync(body);

        await (await owner.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Name = "Renamed", RowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var raftBody = NewRaftBody();
        var raft = await owner.CreateRaftAsync(zone.Id(), raftBody);
        await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", raftBody with { ConditionNotes = "Checked", RowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var zoneAudit = await host.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == zone.Id() && e.Action == "FarmingZone.Updated"));
        Assert.Equal(["Name"], JsonNode.Parse(zoneAudit.After!)!.AsObject().Select(p => p.Key));
        var raftAudit = await host.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == raft.Id() && e.Action == "Raft.Updated"));
        Assert.Equal(["ConditionNotes"], JsonNode.Parse(raftAudit.After!)!.AsObject().Select(p => p.Key));
    }
}
