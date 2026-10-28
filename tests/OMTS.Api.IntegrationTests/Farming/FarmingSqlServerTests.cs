using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
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
        var license = await host.Owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) });

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.Owner.Client.PostAsync(
            $"/api/zone-licenses/{license.Id()}/replace", new LicenseFields { EffectiveDate = Today, ExpiryDate = Today.AddYears(5) }.ToForm())));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            // The pre-check answers MSG-GEN-09 (already Replaced); a loser stopped by the unique index gets MSG-GEN-02.
            Assert.Contains(await refused.ErrorCodeAsync(), new[] { "MSG-GEN-09", "MSG-GEN-02" });
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
            zoneCode, new string('ữ', 200), new string('ả', 500), -89.999999m, 179.999999m, 99_999_999_999_999.9999m, new string('ơ', 200)));
        var savedZone = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}");
        Assert.Equal(zoneCode, savedZone.Text("code"));
        Assert.Equal(new string('ữ', 200), savedZone.Text("name"));
        Assert.Equal(new string('ả', 500), savedZone.Text("location"));
        Assert.Equal(new string('ơ', 200), savedZone.Text("waterMeasurementPoint"));
        Assert.Equal(-89.999999m, savedZone.GetProperty("latitude").GetDecimal());

        var raftCode = NewCode() + string.Concat(Enumerable.Repeat("Bè", 20));
        var raft = await owner.CreateRaftAsync(zone.Id(), new RaftBody(
            raftCode, 21.123456m, 107.654321m, new string('ị', 500), new string('ô', 100), new string('ừ', 2000),
            [new LaneBody(1, 99_999_999.99m, new string('A', 50), new string('B', 50))]));
        var savedRaft = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.Equal(raftCode, savedRaft.Text("code"));
        Assert.Equal(new string('ừ', 2000), savedRaft.Text("conditionNotes"));
        Assert.Equal(new string('ô', 100), savedRaft.Text("size"));
        Assert.Equal(99_999_999.99m, savedRaft.GetProperty("lanes")[0].GetProperty("length").GetDecimal());

        var number = new string('Q', 90) + NewCode();
        var license = await owner.CreateLicenseAsync(new LicenseFields
        {
            DocumentNumber = number, ZoneIds = [zone.Id()], EffectiveDate = Today,
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
        var renamed = await (await owner.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Name = "First edit", RowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
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

        var fields = new LicenseFields { ZoneIds = [zone.Id()], EffectiveDate = Today, File = null };
        var license = await owner.CreateLicenseAsync(fields with { File = Pdf });
        await (await owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { Holder = "First", RowVersion = license.Text("rowVersion") }).ToForm())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("MSG06", await (await owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { Holder = "Stale", RowVersion = license.Text("rowVersion") }).ToForm())).ErrorCodeAsync());
        Assert.Equal("First", (await owner.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{license.Id()}")).Text("holder"));
    }

    // The staff zone filter, license overview, lane checks, condition view and reminder job run as SQL, not in memory.
    [SqlServerFact]
    public async Task Zone_scoped_reads_lane_checks_condition_view_and_reminder_run_on_sql_server()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneA = await owner.CreateZoneAsync();
        var zoneB = await owner.CreateZoneAsync();
        var raft = await owner.CreateRaftAsync(zoneA.Id(), NewRaftBody(lanes: 3));
        await owner.CreateRaftAsync(zoneB.Id());
        var license = await owner.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneA.Id(), zoneB.Id()], EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(30) });
        var staff = await host.StaffInZoneAsync(zoneA.Id());

        Assert.Equal([zoneA.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>("/api/zones")).GetProperty("items").EnumerateArray().Select(z => z.Id()));
        Assert.Equal([zoneA.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>("/api/zones/lookup")).EnumerateArray().Select(z => z.Id()));
        Assert.Equal([zoneA.Id()], (await staff.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Select(z => z.Id()));
        Assert.Equal("MSG-GEN-08", await (await staff.Client.GetAsync($"/api/zones/{zoneB.Id()}/rafts")).ErrorCodeAsync());

        var badges = (await owner.GetFromJsonAsync<JsonElement>("/api/zones")).GetProperty("items").EnumerateArray().Select(z => z.GetProperty("licenseState").GetString()).ToList();
        Assert.Equal(["ExpiringSoon", "ExpiringSoon"], badges);
        var overview = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneA.Id()}/licenses?asOf={Today.AddDays(31):yyyy-MM-dd}");
        Assert.Contains(overview.GetProperty("licenses").EnumerateArray(), r => r.Text("state") == "Expired");

        var cycle = new CultivationCycle { RaftId = raft.Id(), OpenedAt = DateTimeOffset.UtcNow.AddDays(-60), ClosedAt = DateTimeOffset.UtcNow.AddDays(-10), StatusId = StatusIds.CultivationCycle.Closed };
        await host.UseDbAsync(db =>
        {
            db.CultivationCycles.Add(cycle);
            db.LaneSegments.Add(Segment(cycle.Id, raft.LaneId(3), LaneSegmentType.Seeded, 0, 40, Today.AddDays(-50), host.Owner.Account.Id));
            db.LaneSegments.Add(Segment(cycle.Id, raft.LaneId(3), LaneSegmentType.Harvested, 0, 15, Today.AddDays(-20), host.Owner.Account.Id));
        });

        var condition = await staff.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition?date={Today.AddDays(-15):yyyy-MM-dd}");
        Assert.Equal(cycle.Id, condition.GetProperty("cycleId").GetGuid());
        var lane3 = condition.GetProperty("lanes")[2];
        Assert.Equal("PartiallyHarvested", lane3.Text("summary"));
        Assert.Equal(host.Owner.Account.DisplayName, lane3.GetProperty("portions")[0].GetProperty("segments")[0].Text("recordedBy"));

        var blocked = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code"), lanes: 2) with { RowVersion = raft.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal([3], blocked.GetProperty("laneNumbers").EnumerateArray().Select(n => n.GetInt32()));

        var job = host.Api.Services.GetRequiredService<ZoneLicenseReminderJob>();
        Assert.Equal(1, await job.RunOnceAsync(Today));
        Assert.Equal(0, await job.RunOnceAsync(Today));
        var code = license.Id().ToString();
        var recipients = await host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry && n.RelatedRecordCode == code).Select(n => n.RecipientAccountId).ToListAsync());
        Assert.Equal(new[] { host.Owner.Account.Id, staff.Account.Id }.Order(), recipients.Order());
    }

    private static LaneSegment Segment(Guid cycleId, Guid laneId, LaneSegmentType type, decimal from, decimal to, DateOnly date, Guid recordedBy) => new()
    {
        CycleId = cycleId, LaneId = laneId, SegmentType = type, FromPosition = from, ToPosition = to, SegmentDate = date, RecordedByAccountId = recordedBy,
    };

    private static async Task AssertDuplicateAsync(FarmingSqlServerHost host, Action<OMTS.Infrastructure.Persistence.OmtsDbContext> change)
    {
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(change));
        Assert.Contains(((SqlException)failure.InnerException!).Number, new[] { 2601, 2627 });
    }
}
