using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-02/03/04 main flows through the API: zones, rafts with versioned lanes, licenses and the reminder job.</summary>
public class FarmingZoneRaftLicenseFlowTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] Pdf = "%PDF-1.4\n%test license\n"u8.ToArray();

    [Fact]
    public async Task Owner_manages_a_zone_and_staff_only_see_their_zones()
    {
        var owner = await OwnerAsync();
        var zone = await CreateZoneAsync(owner);
        var zoneId = zone.GetProperty("id").GetGuid();

        var duplicate = await owner.PostAsJsonAsync("/api/zones", ZoneBody(zone.GetProperty("code").GetString()!.ToLowerInvariant()));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("MSG-GEN-02", await duplicate.ErrorCodeAsync());
        Assert.Equal(zoneId, (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingZoneId").GetGuid());

        var invalid = await owner.PostAsJsonAsync("/api/zones", ZoneBody(NewCode()) with { Latitude = 91, Longitude = null });
        Assert.Equal("MSG-GEN-01", await invalid.ErrorCodeAsync());
        var fields = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("Latitude", fields);
        Assert.Contains("Longitude", fields);

        var updated = await owner.PutAsJsonAsync($"/api/zones/{zoneId}", ZoneBody(NewCode()) with { RowVersion = zone.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        // InMemory does not generate rowversions: a version the server never issued stands for a stale one.
        var stale = await owner.PutAsJsonAsync($"/api/zones/{zoneId}", ZoneBody(NewCode()) with { RowVersion = "AAAAAAAAB9E=" });
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());

        // A Zone Manager of another zone: no write, no read of this zone, not in the lookup.
        var otherZone = await factory.AddZoneAsync(NewCode());
        var staff = await StaffInZoneAsync(otherZone.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync("/api/zones", ZoneBody(NewCode()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/zones/{zoneId}")).StatusCode);
        var lookup = await staff.GetFromJsonAsync<JsonElement>("/api/zones/lookup");
        Assert.Equal([otherZone.Id], lookup.EnumerateArray().Select(z => z.GetProperty("id").GetGuid()).ToList());

        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "FarmingZone.Created" && e.RecordId == zoneId)));
    }

    [Fact]
    public async Task Raft_layout_is_versioned_and_blocked_while_a_cycle_is_open()
    {
        var owner = await OwnerAsync();
        var zoneId = (await CreateZoneAsync(owner)).GetProperty("id").GetGuid();

        var created = await owner.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", RaftBody(NewCode(), lanes: 5));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var raft = await created.Content.ReadFromJsonAsync<JsonElement>();
        var raftId = raft.GetProperty("id").GetGuid();
        Assert.Equal(1, raft.GetProperty("layoutVersion").GetInt32());
        // No purchase receipt is linked to the raft yet (INP-03).
        Assert.Equal(0, raft.GetProperty("materialCost").GetDecimal());

        var gap = await owner.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", RaftBody(NewCode(), lanes: 2) with
        {
            Lanes = [new(1, 10, "A", "B"), new(3, 10, "A", "B")],
        });
        Assert.Equal("MSG-GEN-01", await gap.ErrorCodeAsync());

        // A zone with In Use rafts cannot be discontinued.
        var discontinueZone = await owner.PostAsJsonAsync($"/api/zones/{zoneId}/discontinue", new { rowVersion = await ZoneRowVersionAsync(owner, zoneId) });
        Assert.Equal("MSG-GEN-06", await discontinueZone.ErrorCodeAsync());

        var relaid = await owner.PutAsJsonAsync($"/api/rafts/{raftId}", RaftBody(raft.GetProperty("code").GetString()!, lanes: 6) with
        {
            RowVersion = raft.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal(HttpStatusCode.OK, relaid.StatusCode);
        var second = await relaid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, second.GetProperty("layoutVersion").GetInt32());
        Assert.Equal(6, second.GetProperty("lanes").GetArrayLength());

        await factory.UseDbAsync(db => db.CultivationCycles.Add(new CultivationCycle { RaftId = raftId, OpenedAt = DateTimeOffset.UtcNow.AddDays(-1) }));
        var blocked = await owner.PutAsJsonAsync($"/api/rafts/{raftId}", RaftBody(second.GetProperty("code").GetString()!, lanes: 4) with
        {
            RowVersion = second.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal("MSG-GEN-06", await blocked.ErrorCodeAsync());
        var discontinue = await owner.PostAsJsonAsync($"/api/rafts/{raftId}/discontinue", new { rowVersion = second.GetProperty("rowVersion").GetString() });
        Assert.Equal("MSG-GEN-06", await discontinue.ErrorCodeAsync());

        var condition = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/condition");
        Assert.Equal(6, condition.GetProperty("lanes").GetArrayLength());
        Assert.All(condition.GetProperty("lanes").EnumerateArray(), lane => Assert.Equal("NotSeeded", lane.GetProperty("summary").GetString()));
    }

    [Fact]
    public async Task License_reminder_sends_one_notice_per_recipient_and_none_on_a_second_run()
    {
        var owner = await OwnerAsync();
        var zoneId = (await CreateZoneAsync(owner)).GetProperty("id").GetGuid();
        var today = BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow);

        var created = await owner.PostAsync("/api/zone-licenses", LicenseForm(zoneId, today.AddDays(-300), today.AddDays(30)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var license = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ExpiringSoon", license.GetProperty("state").GetString());

        var overview = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/licenses");
        var rows = overview.GetProperty("licenses").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("state").GetString() == "Missing" && r.GetProperty("documentType").GetString() == "SEA_AREA_ALLOCATION");

        var job = factory.Services.GetRequiredService<ZoneLicenseReminderJob>();
        await job.RunOnceAsync(today);
        await job.RunOnceAsync(today);
        var licenseId = license.GetProperty("id").GetGuid().ToString();
        var notices = await factory.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry && n.RelatedRecordCode == licenseId).ToListAsync());
        Assert.NotEmpty(notices);
        Assert.Equal(notices.Count, notices.Select(n => n.RecipientAccountId).Distinct().Count());
        Assert.Contains("expires in 30 days", notices[0].Body);

        // Replaced: the old license is never reminded again, and editing it is refused.
        var replaced = await owner.PostAsync($"/api/zone-licenses/{licenseId}/replace", LicenseForm(null, today, today.AddYears(5)));
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);
        var edit = await owner.PutAsync($"/api/zone-licenses/{licenseId}", LicenseForm(zoneId, today.AddDays(-300), today.AddDays(40), license.GetProperty("rowVersion").GetString()));
        Assert.Equal("MSG-GEN-09", await edit.ErrorCodeAsync());
        var again = await owner.PostAsync($"/api/zone-licenses/{licenseId}/replace", LicenseForm(null, today, today.AddYears(5)));
        Assert.Equal("MSG-GEN-09", await again.ErrorCodeAsync());
    }

    private async Task<HttpClient> OwnerAsync()
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        return await factory.LoginAsync(owner.Username);
    }

    private async Task<HttpClient> StaffInZoneAsync(Guid zoneId)
    {
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var role = StaffRole.ZoneManager;
        await factory.GrantAsync(staff, role, await factory.QueryAsync(db => db.FarmingZones.SingleAsync(z => z.Id == zoneId)));
        return await factory.LoginAsync(staff.Username);
    }

    private static async Task<JsonElement> CreateZoneAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/zones", ZoneBody(NewCode()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ZoneRowVersionAsync(HttpClient owner, Guid zoneId) =>
        (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}")).GetProperty("rowVersion").GetString()!;

    private static ZoneBodyDto ZoneBody(string code) => new(code, $"Zone {code}", "Bai Tu Long bay", 21.05m, 107.35m, 12.5m, "Buoy 3");

    private static RaftBodyDto RaftBody(string code, int lanes) => new(
        code, 21.05m, 107.35m, "Raft row 2", "40 m x 20 m", null,
        Enumerable.Range(1, lanes).Select(n => new LaneDto(n, 40, "A", "B")).ToList());

    private static MultipartFormDataContent LicenseForm(Guid? zoneId, DateOnly effective, DateOnly expiry, string? rowVersion = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("AQUACULTURE_LICENSE"), "documentType" },
            { new StringContent($"GP-{NewCode()}"), "documentNumber" },
            { new StringContent(effective.ToString("yyyy-MM-dd")), "effectiveDate" },
            { new StringContent(expiry.ToString("yyyy-MM-dd")), "expiryDate" },
        };
        if (zoneId is not null)
        {
            form.Add(new StringContent(zoneId.Value.ToString()), "zoneIds");
        }

        if (rowVersion is not null)
        {
            form.Add(new StringContent(rowVersion), "rowVersion");
        }

        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "license.pdf");
        return form;
    }

    private sealed record ZoneBodyDto(
        string Code, string Name, string Location, decimal? Latitude, decimal? Longitude, decimal Area, string WaterMeasurementPoint, string? RowVersion = null);

    private sealed record LaneDto(int LaneNumber, decimal Length, string MarkerA, string MarkerB);

    private sealed record RaftBodyDto(
        string Code, decimal Latitude, decimal Longitude, string Location, string Size, string? ConditionNotes, IReadOnlyList<LaneDto> Lanes, string? RowVersion = null);
}
