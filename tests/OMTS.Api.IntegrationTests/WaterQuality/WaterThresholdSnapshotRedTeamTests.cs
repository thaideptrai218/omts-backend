using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01/02/04 threshold copy and evaluation (QA red team): the copy is the set current when the measurement was saved,
/// corrections keep it, bounds are inclusive, an unset bound is never checked and values with more than two decimals are
/// refused instead of rounded.
/// </summary>
public class WaterThresholdSnapshotRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task The_copy_is_the_set_current_when_the_measurement_is_saved_after_the_recognition()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var keyOne = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var keyTwo = await z.Owner.PhotoKeyAsync(z.Zone.Id);

        await SetPhAsync(z, 6.5m, 9.0m);
        var underWider = await SavedAsync(await z.RecordAsync(8.8m, 20m, photoKey: keyOne));
        await SetPhAsync(z, 6.5m, 8.0m);
        var underNarrower = await SavedAsync(await z.RecordAsync(8.4m, 20m, photoKey: keyTwo));

        Assert.Equal("None", underWider.Text("alertState"));
        Assert.Equal(9.0m, underWider.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Empty(await api.AlertsOfAsync(underWider.Id()));
        Assert.Equal("Active", underNarrower.Text("alertState"));
        Assert.Equal("pH 8.40 > 8.00", underNarrower.Text("violatedValues"));
        Assert.Single(await api.AlertsOfAsync(underNarrower.Id()), n => n.RecipientAccountId == manager.Id);
    }

    [Fact]
    public async Task Only_minimums_set_checks_the_minimums_and_never_the_missing_maximums()
    {
        var z = await api.ZoneAsync();
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone =>
        {
            (zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax) = (6.5m, null, 15m, null);
            (zone.TemperatureMin, zone.TemperatureMax) = (10m, null);
        });

        var below = await z.RecordedAsync(6.49m, 14.99m, temperature: 9.99m);
        var farAbove = await z.RecordedAsync(14m, 50m, temperature: 45m);

        Assert.Equal("Active", below.Text("alertState"));
        Assert.Equal("pH 6.49 < 6.50; Salinity 14.99 < 15.00; Temperature 9.99 < 10.00", below.Text("violatedValues"));
        Assert.Equal("None", farAbove.Text("alertState"));
        Assert.Empty(await api.AlertsOfAsync(farAbove.Id()));
        Assert.Equal(JsonValueKind.Null, farAbove.GetProperty("thresholdsUsed").GetProperty("phMax").ValueKind);
    }

    [Fact]
    public async Task Zone_without_thresholds_saves_an_empty_copy_and_no_alert()
    {
        var z = await api.ZoneAsync(thresholds: false);

        var body = await z.RecordedAsync(0m, 50m);

        Assert.Equal("Active", body.Text("status"));
        Assert.Equal("None", body.Text("alertState"));
        Assert.False(body.GetProperty("thresholdExceeded").GetBoolean());
        var used = body.GetProperty("thresholdsUsed");
        foreach (var bound in new[] { "phMin", "phMax", "salinityMin", "salinityMax", "temperatureMin", "temperatureMax" })
        {
            Assert.Equal(JsonValueKind.Null, used.GetProperty(bound).ValueKind);
        }

        Assert.Empty(await api.AlertsOfAsync(body.Id()));
    }

    [Fact]
    public async Task Widest_thresholds_are_accepted_and_values_equal_to_them_raise_no_alert()
    {
        var z = await api.ZoneAsync();

        var saved = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new
        {
            phMin = 0m, phMax = 14m, salinityMin = 0m, salinityMax = 50m, temperatureMin = 0m, temperatureMax = 45m,
        });
        var low = await z.RecordedAsync(0m, 0m, temperature: 0m);
        var high = await z.RecordedAsync(14m, 50m, temperature: 45m);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("None", low.Text("alertState"));
        Assert.Equal("None", high.Text("alertState"));
    }

    [Fact]
    public async Task Values_with_more_than_two_decimals_are_refused_not_rounded()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);

        var record = await z.RecordAsync(7.125m, 20m);
        var salinity = await z.RecordAsync(7m, 20.005m);
        var temperature = await z.RecordAsync(7m, 20m, 25.005m);
        var correct = await z.Owner.CorrectAsync(saved.Id(), 7.125m, 20m);
        var threshold = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6.555m, phMax = 8.5m, salinityMin = 15m, salinityMax = 30m });

        Assert.Equal("pH", await FieldAsync(record));
        Assert.Equal("Salinity", await FieldAsync(salinity));
        Assert.Equal("Temperature", await FieldAsync(temperature));
        Assert.Equal("pH", await FieldAsync(correct));
        Assert.Equal("pH minimum", await FieldAsync(threshold));
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
        var zone = await api.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(x => x.Id == z.Zone.Id));
        Assert.Equal(6.5m, zone.PhMin);
    }

    [Fact]
    public async Task Cooperative_wide_MSG_WATER_05_keeps_every_zone_unchanged_and_writes_no_audit()
    {
        await using var own = new WaterApiFactory();
        var a = await own.ZoneAsync();
        var b = await own.ZoneAsync();

        var response = await a.Owner.PutAsJsonAsync("/api/water-thresholds", new { phMin = 7m, phMax = 8m, salinityMin = 30m, salinityMax = 30m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-WATER-05", await response.ErrorCodeAsync());
        Assert.Equal("Salinity minimum", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        var zones = await own.QueryAsync(db => db.FarmingZones.AsNoTracking().ToListAsync());
        Assert.All(zones, zone => Assert.Equal((6.5m, 8.5m, 15m, 30m), (zone.PhMin!.Value, zone.PhMax!.Value, zone.SalinityMin!.Value, zone.SalinityMax!.Value)));
        Assert.Equal(0, await own.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "WaterThreshold.Updated")));
        Assert.NotEqual(a.Zone.Id, b.Zone.Id);
    }

    [Fact]
    public async Task Corrections_keep_the_thresholds_of_the_measurement_when_the_zone_changes_in_between()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(8.0m, 20m);
        await SetPhAsync(z, 6.5m, 9.5m);
        var first = await SavedOkAsync(await z.Owner.CorrectAsync(saved.Id(), 8.4m, 20m));
        await SetPhAsync(z, 6.5m, 8.0m);

        var second = await SavedOkAsync(await z.Owner.CorrectAsync(saved.Id(), 8.6m, 20m));

        Assert.Equal("None", first.Text("alertState"));
        Assert.Equal(8.5m, first.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Equal("Active", second.Text("alertState"));
        Assert.Equal("pH 8.60 > 8.50", second.Text("violatedValues"));
        Assert.Equal(8.5m, second.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Single(await api.AlertsOfAsync(saved.Id()), n => n.RecipientAccountId == manager.Id);
    }

    private static async Task SetPhAsync(WaterZone z, decimal min, decimal max)
    {
        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = min, phMax = max, salinityMin = 15m, salinityMax = 30m });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> SavedAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> SavedOkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> FieldAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field");
    }
}
