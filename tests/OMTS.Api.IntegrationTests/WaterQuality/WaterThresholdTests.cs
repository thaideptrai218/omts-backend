using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>WATER-04 – thresholds of pH, salinity and temperature per zone, Owner only; saved measurements keep their copy.</summary>
public class WaterThresholdTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Past_alert_keeps_the_old_maximum_and_a_new_measurement_uses_the_new_one()
    {
        var z = await api.ZoneAsync();
        var pastAlert = await z.RecordedAsync(8.8m, 20m);

        var saved = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6.5m, phMax = 9.0m, salinityMin = 15m, salinityMax = 30m, temperatureMin = 12m, temperatureMax = 33m });
        var newMeasurement = await z.RecordedAsync(8.8m, 20m);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var thresholds = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("thresholds");
        Assert.Equal((9.0m, 12m, 33m), (thresholds.GetProperty("phMax").GetDecimal(), thresholds.GetProperty("temperatureMin").GetDecimal(), thresholds.GetProperty("temperatureMax").GetDecimal()));
        var past = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{pastAlert.Id()}");
        Assert.Equal(8.5m, past.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Equal(35m, past.GetProperty("thresholdsUsed").GetProperty("temperatureMax").GetDecimal());
        Assert.Equal("Active", past.Text("alertState"));
        Assert.Equal("None", newMeasurement.Text("alertState"));
        Assert.Equal(33m, newMeasurement.GetProperty("thresholdsUsed").GetProperty("temperatureMax").GetDecimal());
        Assert.Empty(await api.AlertsOfAsync(newMeasurement.Id()));

        var audit = Assert.Single(await api.AuditOfAsync(z.Zone.Id), e => e.Action == "WaterThreshold.Updated");
        Assert.Contains("\"PhMax\":8.5", audit.Before);
        Assert.Contains("\"PhMax\":9.0", audit.After);
        Assert.Contains("\"TemperatureMax\":33", audit.After);
        Assert.Equal(z.Zone.Id, audit.ZoneId);
    }

    [Fact]
    public async Task Minimum_not_less_than_maximum_is_MSG_WATER_05_and_keeps_the_current_thresholds()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 8.5m, phMax = 8.5m, salinityMin = 15m, salinityMax = 30m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-WATER-05", await response.ErrorCodeAsync());
        Assert.Equal(
            "The minimum of pH must be less than the maximum. The thresholds were not saved. Please correct the values.",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        var zone = await api.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(x => x.Id == z.Zone.Id));
        Assert.Equal((6.5m, 8.5m), (zone.PhMin!.Value, zone.PhMax!.Value));
        Assert.Empty((await api.AuditOfAsync(z.Zone.Id)).Where(e => e.Action == "WaterThreshold.Updated"));
    }

    [Theory]
    [InlineData(6.5, null, 15.0, 30.0, 10.0, 35.0, "pH maximum")]
    [InlineData(6.5, 14.5, 15.0, 30.0, 10.0, 35.0, "pH maximum")]
    [InlineData(6.5, 8.5, -1.0, 30.0, 10.0, 35.0, "Salinity minimum")]
    [InlineData(6.5, 8.5, 15.0, 50.5, 10.0, 35.0, "Salinity maximum")]
    [InlineData(6.5, 8.5, 15.0, 30.0, -1.0, 35.0, "Temperature minimum")]
    [InlineData(6.5, 8.5, 15.0, 30.0, 10.0, 45.5, "Temperature maximum")]
    [InlineData(6.5, 8.5, 15.0, 30.0, 10.0, null, "Temperature maximum")]
    [InlineData(6.5, 8.5, 15.0, 30.0, null, 35.0, "Temperature minimum")]
    public async Task Value_out_of_range_or_half_a_pair_is_MSG01(double? phMin, double? phMax, double? salMin, double? salMax, double? tempMin, double? tempMax, string field)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin, phMax, salinityMin = salMin, salinityMax = salMax, temperatureMin = tempMin, temperatureMax = tempMax });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Fact]
    public async Task Pairs_can_be_cleared_together_and_an_omitted_pair_is_cleared()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6.0m, phMax = 9.0m, salinityMin = (decimal?)null, salinityMax = (decimal?)null });

        var thresholds = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("thresholds");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, thresholds.GetProperty("salinityMin").ValueKind);
        Assert.Equal(JsonValueKind.Null, thresholds.GetProperty("temperatureMax").ValueKind);
        Assert.Equal(6.0m, thresholds.GetProperty("phMin").GetDecimal());
    }

    [Fact]
    public async Task Same_thresholds_for_every_in_use_zone_in_one_call()
    {
        await using var own = new WaterApiFactory();
        var a = await own.ZoneAsync();
        var b = await own.ZoneAsync(thresholds: false);
        var discontinued = await own.ZoneAsync();
        await own.UpdateAsync<FarmingZone>(discontinued.Zone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);

        var response = await a.Owner.PutAsJsonAsync("/api/water-thresholds", new { phMin = 7m, phMax = 8m, salinityMin = 18m, salinityMax = 28m, temperatureMin = 11m, temperatureMax = 31m });
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, list.GetArrayLength());
        var zones = await own.QueryAsync(db => db.FarmingZones.AsNoTracking().ToListAsync());
        Assert.All(zones.Where(z => z.Id != discontinued.Zone.Id), z => Assert.Equal((7m, 8m, 18m, 28m, 11m, 31m), (z.PhMin!.Value, z.PhMax!.Value, z.SalinityMin!.Value, z.SalinityMax!.Value, z.TemperatureMin!.Value, z.TemperatureMax!.Value)));
        Assert.Equal(8.5m, zones.Single(z => z.Id == discontinued.Zone.Id).PhMax);
        var audited = await own.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "WaterThreshold.Updated").Select(e => e.RecordId).ToListAsync());
        Assert.Equal(new[] { a.Zone.Id, b.Zone.Id }.Order(), audited.Select(id => id!.Value).Order());
    }

    // WATER-04 step 1 and GBR-01: the list has the zones In Use; a Discontinued zone is read-only.
    [Fact]
    public async Task A_discontinued_zone_is_left_out_of_the_list_and_cannot_be_set_but_can_still_be_read()
    {
        await using var own = new WaterApiFactory();
        var z = await own.ZoneAsync();
        var kept = await own.ZoneAsync();
        await own.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        var list = await z.Owner.GetFromJsonAsync<JsonElement>("/api/water-thresholds");
        var set = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6m, phMax = 9m });
        var read = await z.Owner.GetAsync($"/api/zones/{z.Zone.Id}/water-thresholds");

        Assert.Equal([kept.Zone.Id], list.EnumerateArray().Select(t => t.GetProperty("zoneId").GetGuid()));
        Assert.Equal(HttpStatusCode.Conflict, set.StatusCode);
        Assert.Equal("MSG-GEN-09", await set.ErrorCodeAsync());
        Assert.Equal("Farming zone is Discontinued and cannot be used for this action.", (await set.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(8.5m, (await own.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(x => x.Id == z.Zone.Id))).PhMax);
    }

    [Fact]
    public async Task Staff_with_view_reads_the_thresholds_of_their_zones_only_and_cannot_set_them()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var (viewer, _) = await api.StaffAsync(z.Zone);
        var (noWater, _) = await api.StaffAsync(z.Zone, StaffRole.Accountant);

        var own = await viewer.GetAsync($"/api/zones/{z.Zone.Id}/water-thresholds");
        var foreign = await viewer.GetAsync($"/api/zones/{other.Zone.Id}/water-thresholds");
        var list = await viewer.GetFromJsonAsync<JsonElement>("/api/water-thresholds");
        var set = await viewer.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 1m, phMax = 2m });
        var setAll = await viewer.PutAsJsonAsync("/api/water-thresholds", new { phMin = 1m, phMax = 2m });
        var listWithoutWater = await noWater.GetAsync("/api/water-thresholds");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal([z.Zone.Id], list.EnumerateArray().Select(t => t.GetProperty("zoneId").GetGuid()));
        foreach (var response in new[] { set, setAll, listWithoutWater })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task Unknown_zone_is_404_for_the_owner()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{Guid.NewGuid()}/water-thresholds", new { phMin = 6m, phMax = 9m });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
