using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// Regressions of product bugs found in the OMTS-210 QA review (fixed in this PR), on SQL Server:
/// a lane layout change does not move the record's rowversion, markers in Vietnamese overflow their column, and SQL
/// Server decimals are audited as changed when they are not. Also the GFR-03 reminder query of PR 0.8 on SQL Server.
/// </summary>
public class FarmingSqlServerRegressionTests
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

    // GFR-03 on SQL Server: the candidate query reads the zone status and the replacements as SQL; a discontinued zone is
    // skipped until it is resumed, and the replacement's record time (datetimeoffset) makes the old license Replaced today.
    [SqlServerFact]
    public async Task Reminder_skips_replaced_licenses_and_zones_not_in_use_on_sql_server()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var inUse = await owner.CreateZoneAsync();
        var stopped = await owner.CreateZoneAsync();
        var replacedZone = (await owner.CreateZoneAsync()).Id();
        var due = await owner.CreateLicenseAsync(new LicenseFields { ZoneId = inUse.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(10) });
        var paused = await owner.CreateLicenseAsync(new LicenseFields { ZoneId = stopped.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(10) });
        var old = await owner.CreateLicenseAsync(new LicenseFields { ZoneId = replacedZone, EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(10) });
        await (await owner.PostAsync($"/api/zone-licenses/{old.Id()}/replace", new LicenseFields { EffectiveDate = Today.AddDays(11) }.ToForm())).ReadAsync(HttpStatusCode.Created);
        // A license save is also a save of its zone (a parallel discontinue fails with MSG06), so the zone is read again.
        Assert.Equal("MSG06", await (await owner.PostAsJsonAsync($"/api/zones/{stopped.Id()}/discontinue", new { rowVersion = stopped.Text("rowVersion") })).ErrorCodeAsync());
        var current = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{stopped.Id()}");
        var discontinued = await (await owner.PostAsJsonAsync($"/api/zones/{stopped.Id()}/discontinue", new { rowVersion = current.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var job = host.Api.Services.GetRequiredService<ZoneLicenseReminderJob>();

        Assert.Equal(1, await job.RunOnceAsync(Today));
        Assert.Equal("Replaced", (await owner.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{old.Id()}")).Text("state"));
        Assert.Equal([due.Id()], await RemindedAsync());

        await (await owner.PostAsJsonAsync($"/api/zones/{stopped.Id()}/resume", new { rowVersion = discontinued.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, await job.RunOnceAsync(Today));
        Assert.Equal(0, await job.RunOnceAsync(Today));
        Assert.Equal(new[] { due.Id(), paused.Id() }.Order(), (await RemindedAsync()).Order());

        Task<List<Guid>> RemindedAsync() => host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry).Select(n => n.RelatedRecordId!.Value).Distinct().ToListAsync());
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
