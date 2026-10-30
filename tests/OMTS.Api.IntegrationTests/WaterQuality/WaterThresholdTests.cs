using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>ENV-04 – thresholds per zone, Owner only; old alerts keep the thresholds they used.</summary>
public class WaterThresholdTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Past_alert_keeps_the_old_maximum_and_a_new_confirmation_uses_the_new_one()
    {
        var z = await api.ZoneAsync();
        var pastAlert = await z.RecordedAsync(8.8m, 20m);

        var saved = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6.5m, phMax = 9.0m, salinityMin = 15m, salinityMax = 30m });
        var newMeasurement = await z.RecordedAsync(8.8m, 20m);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(9.0m, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("thresholds").GetProperty("phMax").GetDecimal());
        var past = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{pastAlert.Id()}");
        Assert.Equal(8.5m, past.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Equal("Active", past.Text("alertState"));
        Assert.Equal("None", newMeasurement.Text("alertState"));
        Assert.Empty(await api.AlertsOfAsync(newMeasurement.Id()));

        var audit = Assert.Single(await api.AuditOfAsync(z.Zone.Id), e => e.Action == "WaterThreshold.Updated");
        Assert.Contains("\"PhMax\":8.5", audit.Before);
        Assert.Contains("\"PhMax\":9.0", audit.After);
        Assert.Equal(z.Zone.Id, audit.ZoneId);
    }

    [Fact]
    public async Task Minimum_not_less_than_maximum_is_MSG28_and_keeps_the_current_thresholds()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 8.5m, phMax = 8.5m, salinityMin = 15m, salinityMax = 30m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG28", await response.ErrorCodeAsync());
        Assert.Equal("The minimum value must be less than the maximum value.", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        var zone = await api.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(x => x.Id == z.Zone.Id));
        Assert.Equal((6.5m, 8.5m), (zone.PhMin!.Value, zone.PhMax!.Value));
        Assert.Empty((await api.AuditOfAsync(z.Zone.Id)).Where(e => e.Action == "WaterThreshold.Updated"));
    }

    [Theory]
    [InlineData(6.5, null, 15.0, 30.0, "pH maximum")]
    [InlineData(6.5, 14.5, 15.0, 30.0, "pH maximum")]
    [InlineData(6.5, 8.5, -1.0, 30.0, "Salinity minimum")]
    [InlineData(6.5, 8.5, 15.0, 100.5, "Salinity maximum")]
    public async Task Value_out_of_range_or_half_a_pair_is_MSG01(double? phMin, double? phMax, double? salMin, double? salMax, string field)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin, phMax, salinityMin = salMin, salinityMax = salMax });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Fact]
    public async Task Pairs_can_be_cleared_together()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6.0m, phMax = 9.0m, salinityMin = (decimal?)null, salinityMax = (decimal?)null });

        var thresholds = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("thresholds");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, thresholds.GetProperty("salinityMin").ValueKind);
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

        var response = await a.Owner.PutAsJsonAsync("/api/water-thresholds", new { phMin = 7m, phMax = 8m, salinityMin = 18m, salinityMax = 28m });
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, list.GetArrayLength());
        var zones = await own.QueryAsync(db => db.FarmingZones.AsNoTracking().ToListAsync());
        Assert.All(zones.Where(z => z.Id != discontinued.Zone.Id), z => Assert.Equal((7m, 8m, 18m, 28m), (z.PhMin!.Value, z.PhMax!.Value, z.SalinityMin!.Value, z.SalinityMax!.Value)));
        Assert.Equal(8.5m, zones.Single(z => z.Id == discontinued.Zone.Id).PhMax);
        var audited = await own.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "WaterThreshold.Updated").Select(e => e.RecordId).ToListAsync());
        Assert.Equal(new[] { a.Zone.Id, b.Zone.Id }.Order(), audited.Select(id => id!.Value).Order());
    }

    [Fact]
    public async Task Staff_with_view_reads_the_thresholds_of_their_zones_only_and_cannot_set_them()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var (viewer, _) = await api.StaffAsync(z.Zone, create: true, edit: true);
        var (noWater, _) = await api.StaffAsync(z.Zone, view: false);

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
            Assert.Equal("MSG02", await response.ErrorCodeAsync());
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
