using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>WATER-03 step 1 and BR-WATER-04: the zones the caller can access, with the latest measurement time and the water alert worked out each time.</summary>
public class WaterZoneOverviewTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private const string Url = "/api/water-measurements/zones";

    [Fact]
    public async Task A_zone_never_measured_has_no_time_and_no_alert()
    {
        var z = await api.ZoneAsync();

        var row = await RowAsync(z);

        Assert.Equal((z.Zone.Code, z.Zone.Name, "In Use"), (row.Text("zoneCode"), row.Text("zoneName"), row.Text("zoneStatus")));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("latestMeasuredAt").ValueKind);
        Assert.False(row.GetProperty("hasWaterAlert").GetBoolean());
        Assert.Equal(0, row.GetProperty("valuesOutside").GetArrayLength());
    }

    // "Latest" means the latest measurement time, not the time the record was saved.
    [Fact]
    public async Task The_alert_follows_the_latest_measurement_time_not_the_last_save()
    {
        var z = await api.ZoneAsync();
        var measured = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
        await z.RecordedAsync(7m, 20m, measuredAt: measured);
        await z.RecordedAsync(9.5m, 20m, measuredAt: measured.AddDays(-5));

        var row = await RowAsync(z);

        Assert.Equal(measured, row.GetProperty("latestMeasuredAt").GetDateTimeOffset());
        Assert.False(row.GetProperty("hasWaterAlert").GetBoolean());
    }

    [Fact]
    public async Task A_latest_measurement_outside_is_an_alert_naming_the_values_and_a_void_or_correction_changes_it()
    {
        var z = await api.ZoneAsync();
        var measured = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds());
        await z.RecordedAsync(7m, 20m, measuredAt: measured.AddDays(-3));
        var latest = await z.RecordedAsync(9.5m, 12m, measuredAt: measured);

        var outside = await RowAsync(z);
        Assert.True(outside.GetProperty("hasWaterAlert").GetBoolean());
        Assert.Equal(["PH", "SALINITY"], outside.GetProperty("valuesOutside").EnumerateArray().Select(v => v.Text("indicator")));
        Assert.Equal((8.5m, "High"), (outside.GetProperty("valuesOutside")[0].GetProperty("max").GetDecimal(), outside.GetProperty("valuesOutside")[0].Text("direction")));

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.CorrectAsync(latest.Id(), 7.1m, 20m, measuredAt: measured)).StatusCode);
        Assert.False((await RowAsync(z)).GetProperty("hasWaterAlert").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.CorrectAsync(latest.Id(), 9.5m, 20m, measuredAt: measured)).StatusCode);
        Assert.True((await RowAsync(z)).GetProperty("hasWaterAlert").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(latest.Id())).StatusCode);
        var afterVoid = await RowAsync(z);
        Assert.False(afterVoid.GetProperty("hasWaterAlert").GetBoolean());
        Assert.Equal(measured.AddDays(-3), afterVoid.GetProperty("latestMeasuredAt").GetDateTimeOffset());
    }

    // AC-04: the measurement keeps its thresholds, so a later change of the zone's does not touch the alert.
    [Fact]
    public async Task The_alert_uses_the_thresholds_of_the_measurement_not_the_current_ones()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 12m);
        var lowered = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6.5m, phMax = 8.5m, salinityMin = 10m, salinityMax = 30m });
        Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);

        var row = await RowAsync(z);

        Assert.True(row.GetProperty("hasWaterAlert").GetBoolean());
        Assert.Equal(15m, row.GetProperty("valuesOutside")[0].GetProperty("min").GetDecimal());
    }

    // BR-WATER-04: only a zone In Use has a water alert; GBR-01: a Discontinued zone stays readable.
    [Fact]
    public async Task A_discontinued_zone_keeps_its_time_has_no_alert_and_in_use_only_leaves_it_out()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(9.5m, 20m);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        var row = await RowAsync(z);
        var inUse = await z.Owner.GetFromJsonAsync<JsonElement>($"{Url}?inUseOnly=true");

        Assert.Equal("Discontinued", row.Text("zoneStatus"));
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("latestMeasuredAt").ValueKind);
        Assert.False(row.GetProperty("hasWaterAlert").GetBoolean());
        Assert.DoesNotContain(inUse.EnumerateArray(), r => r.GetProperty("zoneId").GetGuid() == z.Zone.Id);
    }

    // §I.4.4.
    [Fact]
    public async Task A_zone_manager_sees_the_zones_of_their_pairs_all_zones_sees_every_one_others_get_MSG_GEN_08()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var (accountant, _) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var allZones = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(allZones, StaffRole.ZoneManager, zone: null);

        var own = await manager.GetFromJsonAsync<JsonElement>(Url);
        var everywhere = await (await api.LoginAsync(allZones.Username)).GetFromJsonAsync<JsonElement>(Url);
        var refused = await accountant.GetAsync(Url);

        Assert.Equal([z.Zone.Id], own.EnumerateArray().Select(r => r.GetProperty("zoneId").GetGuid()));
        Assert.Contains(everywhere.EnumerateArray(), r => r.GetProperty("zoneId").GetGuid() == other.Zone.Id);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(Url)).StatusCode);
    }

    private static async Task<JsonElement> RowAsync(WaterZone z) =>
        (await z.Owner.GetFromJsonAsync<JsonElement>(Url)).EnumerateArray().Single(r => r.GetProperty("zoneId").GetGuid() == z.Zone.Id);
}
