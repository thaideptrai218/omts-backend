using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-02/03/04 behaviour that needs real SQL Server: unique indexes, column sizes, rowversion and query translation.</summary>
public class FarmingSqlServerTests
{
    [SqlServerFact]
    public async Task Zone_code_and_raft_code_per_zone_are_unique_ignoring_case_in_the_database()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var zone = host.NewZoneEntity("ZONE-UQ");
        var other = host.NewZoneEntity("ZONE-OTHER");
        await host.UseDbAsync(db => db.FarmingZones.AddRange(zone, other));

        await AssertDuplicateAsync(host, db => db.FarmingZones.Add(host.NewZoneEntity("zone-uq")));
        await host.UseDbAsync(db => db.Rafts.Add(new Raft { ZoneId = zone.Id, Code = "R-1" }));
        await AssertDuplicateAsync(host, db => db.Rafts.Add(new Raft { ZoneId = zone.Id, Code = "r-1" }));
        await host.UseDbAsync(db => db.Rafts.Add(new Raft { ZoneId = other.Id, Code = "R-1" }));

        // Parallel creates of one code: exactly one zone, the others are told it exists.
        var code = $"PAR-{NewCode()}";
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            host.Owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody(i % 2 == 0 ? code : code.ToLowerInvariant()))));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-GEN-02", await refused.ErrorCodeAsync());
        }

        Assert.Equal(1, await host.QueryAsync(db => db.FarmingZones.CountAsync(z => z.Code == code)));
    }

    // FARM-03: a license is replaced at most once, even by parallel requests, and the loser's upload is removed.
    [SqlServerFact]
    public async Task Parallel_replaces_keep_one_replacement_and_leave_no_orphan_file()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var zoneId = (await host.Owner.Client.CreateZoneAsync()).Id();
        var license = await host.Owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zoneId, EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) });

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.Owner.Client.PostAsync(
            $"/api/zone-licenses/{license.Id()}/replace", new LicenseFields { EffectiveDate = Today, ExpiryDate = Today.AddYears(5) }.ToForm())));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            // The pre-check answers MSG-GEN-09 (already Replaced); a loser stopped by the unique index gets MSG-GEN-02, and
            // one stopped by the row version of the zone (part of every license save since PR 0.8) gets MSG06.
            Assert.Contains(await refused.ErrorCodeAsync(), new[] { "MSG-GEN-09", "MSG-GEN-02", "MSG06" });
        }

        var keys = await host.QueryAsync(db => db.ZoneComplianceDocuments.Where(d => d.Id == license.Id() || d.ReplacesDocumentId == license.Id())
            .Select(d => d.EvidenceFileUrl!).ToListAsync());
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys.Order(), host.StoredKeys("zone-licenses"));
    }

    [SqlServerFact]
    public async Task Longest_allowed_values_fit_the_columns()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneCode = NewCode() + new string('Đ', 40);
        var zone = await owner.CreateZoneAsync(new ZoneBody(
            zoneCode, new string('ữ', 200), new string('ả', 500), -89.999999m, 179.999999m, 99_999_999_999_999.9999m));
        var savedZone = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}");
        Assert.Equal(zoneCode, savedZone.Text("code"));
        Assert.Equal(new string('ữ', 200), savedZone.Text("name"));
        Assert.Equal(new string('ả', 500), savedZone.Text("location"));
        Assert.Equal(-89.999999m, savedZone.GetProperty("latitude").GetDecimal());

        var raftCode = NewCode() + string.Concat(Enumerable.Repeat("Bè", 20));
        var raft = await owner.CreateRaftAsync(zone.Id(), new RaftBody(
            raftCode, 21.123456m, 107.654321m, new string('ị', 500), new string('ô', 100), new string('ừ', 2000), 200));
        var savedRaft = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.Equal(raftCode, savedRaft.Text("code"));
        Assert.Equal(new string('ừ', 2000), savedRaft.Text("conditionNotes"));
        Assert.Equal(new string('ô', 100), savedRaft.Text("size"));
        Assert.Equal(200, savedRaft.GetProperty("lanes").GetArrayLength());

        var number = new string('Q', 90) + NewCode();
        var license = await owner.CreateLicenseAsync(new LicenseFields
        {
            DocumentNumber = number, ZoneId = zone.Id(), EffectiveDate = Today,
            IssuingAuthority = new string('Ủ', 200), Holder = new string('Ợ', 200), LegalScope = new string('Ằ', 1000),
        });
        var savedLicense = await owner.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{license.Id()}");
        Assert.Equal(number, savedLicense.Text("documentNumber"));
        Assert.Equal(new string('Ủ', 200), savedLicense.Text("issuingAuthority"));
        Assert.Equal(new string('Ợ', 200), savedLicense.Text("holder"));
        Assert.Equal(new string('Ằ', 1000), savedLicense.Text("legalScope"));
    }

    [SqlServerFact]
    public async Task Stale_row_versions_are_refused_with_MSG06_and_overwrite_nothing()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;

        var body = NewZoneBody();
        var zone = await owner.CreateZoneAsync(body);
        var renamed = await owner.EditZoneAsync(zone.Id(), body with { Name = "First edit", RowVersion = zone.Text("rowVersion") });
        Assert.NotEqual(zone.Text("rowVersion"), renamed.Text("rowVersion"));
        var stale = await (await owner.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Name = "Second edit", RowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG06", stale.Text("code"));
        Assert.Equal(renamed.Text("rowVersion"), stale.Text("currentRowVersion"));
        Assert.Contains(host.Owner.Account.DisplayName, stale.Text("detail"));
        Assert.Equal("MSG06", await (await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ErrorCodeAsync());
        Assert.Equal("First edit", (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}")).Text("name"));

        // Two edits sent at once with the same version: one wins, the other gets MSG06.
        var racing = await Task.WhenAll(new[] { "Race A", "Race B", "Race C" }.Select(name =>
            owner.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Name = name, RowVersion = renamed.Text("rowVersion") })));
        Assert.Single(racing, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var lost in racing.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal("MSG06", await lost.ErrorCodeAsync());
        }

        var raftBody = NewRaftBody();
        var raft = await owner.CreateRaftAsync(zone.Id(), raftBody);
        await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", raftBody with { ConditionNotes = "Checked", RowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("MSG06", await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", raftBody with { ConditionNotes = "Stale", RowVersion = raft.Text("rowVersion") })).ErrorCodeAsync());
        Assert.Equal("MSG06", await (await owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") })).ErrorCodeAsync());
        Assert.Equal("Checked", (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}")).Text("conditionNotes"));

        var fields = new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today, File = null };
        var license = await owner.CreateLicenseAsync(fields with { File = Pdf });
        await (await owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { Holder = "First", RowVersion = license.Text("rowVersion") }).ToForm())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("MSG06", await (await owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { Holder = "Stale", RowVersion = license.Text("rowVersion") }).ToForm())).ErrorCodeAsync());
        Assert.Equal("First", (await owner.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{license.Id()}")).Text("holder"));
    }

    // The staff zone filter, license overview and reminder job run as SQL, not in memory.
    [SqlServerFact]
    public async Task Zone_scoped_reads_and_reminder_run_on_sql_server()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneA = await owner.CreateZoneAsync();
        var zoneB = await owner.CreateZoneAsync();
        await owner.CreateRaftAsync(zoneA.Id());
        await owner.CreateRaftAsync(zoneB.Id());
        var license = await owner.CreateLicenseAsync(new LicenseFields { ZoneId = zoneA.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(30) });
        // Zone B: an older expired license next to a newer valid one (only the newest one counts for the badge; the older
        // one, not Replaced, still gets its Expired reminder).
        var olderExpired = await owner.CreateLicenseAsync(new LicenseFields { ZoneId = zoneB.Id(), EffectiveDate = Today.AddYears(-3), ExpiryDate = Today.AddDays(-30) });
        await owner.CreateLicenseAsync(new LicenseFields { ZoneId = zoneB.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(31) });
        var staff = await host.StaffInZoneAsync(zoneA.Id());

        Assert.Equal([zoneA.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>("/api/zones")).GetProperty("items").EnumerateArray().Select(z => z.Id()));
        Assert.Equal([zoneA.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>("/api/zones/lookup")).EnumerateArray().Select(z => z.Id()));
        Assert.Equal([zoneA.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Select(z => z.Id()));
        Assert.Equal("MSG-GEN-08", await (await staff.Client.GetAsync($"/api/zones/{zoneB.Id()}/rafts")).ErrorCodeAsync());

        var badges = (await owner.GetFromJsonAsync<JsonElement>("/api/zones")).GetProperty("items").EnumerateArray()
            .ToDictionary(z => z.Id(), z => z.GetProperty("licenseState").GetString());
        Assert.Equal(("ExpiringSoon", null), (badges[zoneA.Id()], badges[zoneB.Id()]));
        var overview = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneA.Id()}/licenses?asOf={Today.AddDays(31):yyyy-MM-dd}");
        Assert.Contains(overview.GetProperty("licenses").EnumerateArray(), r => r.Text("state") == "Expired");

        var job = host.Api.Services.GetRequiredService<ZoneLicenseReminderJob>();
        Assert.Equal(2, await job.RunOnceAsync(Today));
        Assert.Equal(0, await job.RunOnceAsync(Today));
        var olderExpiredId = olderExpired.Id();
        Assert.Equal("MSG-FARM-02", await host.QueryAsync(db => db.Notifications
            .Where(n => n.RelatedRecordId == olderExpiredId).Select(n => n.MessageCode).SingleAsync()));
        var licenseId = license.Id();
        var recipients = await host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry && n.RelatedRecordId == licenseId).Select(n => n.RecipientAccountId!.Value).ToListAsync());
        // The Cooperative Owner only (GFR-03): the Zone Manager of the zone is not told.
        Assert.Equal([host.Owner.Account.Id], recipients);
    }

    private static async Task AssertDuplicateAsync(FarmingSqlServerHost host, Action<OMTS.Infrastructure.Persistence.OmtsDbContext> change)
    {
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(change));
        Assert.Contains(((SqlException)failure.InnerException!).Number, new[] { 2601, 2627 });
    }
}
