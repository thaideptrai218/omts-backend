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
/// Regressions of product bugs found in the OMTS-210 QA review, on SQL Server: SQL Server decimals are audited as changed
/// when they are not. Also the GFR-03 reminder query of PR 0.8 on SQL Server.
/// </summary>
public class FarmingSqlServerRegressionTests
{
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
