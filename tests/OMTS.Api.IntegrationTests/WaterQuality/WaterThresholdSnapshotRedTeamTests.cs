using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// ENV-01/02/04 threshold snapshot and evaluation (QA red team): the set used is the one current at the
/// confirmation, a correction chain keeps the first set, bounds are inclusive, an unset bound is never
/// checked and values with more than two decimals are refused instead of rounded.
/// </summary>
public class WaterThresholdSnapshotRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Draft_is_evaluated_with_the_thresholds_current_at_its_confirmation_not_at_its_creation()
    {
        var z = await api.ZoneAsync();
        var raisedLater = await z.RecordedAsync(8.8m, 20m, confirm: false);
        var loweredLater = await z.RecordedAsync(8.4m, 20m, confirm: false);

        await SetPhAsync(z, 6.5m, 9.0m);
        var underWider = await ConfirmAsync(z, raisedLater.Id());
        await SetPhAsync(z, 6.5m, 8.0m);
        var underNarrower = await ConfirmAsync(z, loweredLater.Id());

        Assert.Equal("None", underWider.Text("alertState"));
        Assert.Equal(9.0m, underWider.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Empty(await api.AlertsOfAsync(raisedLater.Id()));
        Assert.Equal("Active", underNarrower.Text("alertState"));
        Assert.Equal("pH 8.40 > 8.00", underNarrower.Text("violatedValues"));
        Assert.Single(await api.AlertsOfAsync(loweredLater.Id()), n => n.RecipientAccountId == z.OwnerAccount.Id);
    }

    [Fact]
    public async Task Only_minimums_set_checks_the_minimums_and_never_the_missing_maximums()
    {
        var z = await api.ZoneAsync();
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => (zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax) = (6.5m, null, 15m, null));

        var below = await z.RecordedAsync(6.49m, 14.99m);
        var farAbove = await z.RecordedAsync(14m, 100m);

        Assert.Equal("Active", below.Text("alertState"));
        Assert.Equal("pH 6.49 < 6.50; Salinity 14.99 < 15.00", below.Text("violatedValues"));
        Assert.Equal("None", farAbove.Text("alertState"));
        Assert.Empty(await api.AlertsOfAsync(farAbove.Id()));
        Assert.Equal(JsonValueKind.Null, farAbove.GetProperty("thresholdsUsed").GetProperty("phMax").ValueKind);
    }

    [Fact]
    public async Task Zone_without_thresholds_confirms_with_an_empty_snapshot_and_no_alert()
    {
        var z = await api.ZoneAsync(thresholds: false);

        var body = await z.RecordedAsync(0m, 100m);

        Assert.Equal("Confirmed", body.Text("status"));
        Assert.Equal("None", body.Text("alertState"));
        Assert.False(body.GetProperty("thresholdExceeded").GetBoolean());
        var used = body.GetProperty("thresholdsUsed");
        foreach (var bound in new[] { "phMin", "phMax", "salinityMin", "salinityMax" })
        {
            Assert.Equal(JsonValueKind.Null, used.GetProperty(bound).ValueKind);
        }

        Assert.Empty(await api.AlertsOfAsync(body.Id()));
    }

    [Fact]
    public async Task Widest_thresholds_are_accepted_and_values_equal_to_them_raise_no_alert()
    {
        var z = await api.ZoneAsync();

        var saved = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 0m, phMax = 14m, salinityMin = 0m, salinityMax = 100m });
        var low = await z.RecordedAsync(0m, 0m);
        var high = await z.RecordedAsync(14m, 100m);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("None", low.Text("alertState"));
        Assert.Equal("None", high.Text("alertState"));
    }

    [Fact]
    public async Task Values_with_more_than_two_decimals_are_refused_not_rounded()
    {
        var z = await api.ZoneAsync();
        var confirmed = await z.RecordedAsync(7m, 20m);

        var record = await z.RecordAsync(7.125m, 20m);
        var salinity = await z.RecordAsync(7m, 20.005m);
        var correct = await z.Owner.CorrectAsync(confirmed.Id(), 7.125m, 20m, recordVersion: 1);
        var threshold = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6.555m, phMax = 8.5m, salinityMin = 15m, salinityMax = 30m });

        Assert.Equal("pH", await FieldAsync(record));
        Assert.Equal("Salinity", await FieldAsync(salinity));
        Assert.Equal("pH", await FieldAsync(correct));
        Assert.Equal("pH minimum", await FieldAsync(threshold));
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
        var zone = await api.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(x => x.Id == z.Zone.Id));
        Assert.Equal(6.5m, zone.PhMin);
    }

    [Fact]
    public async Task Cooperative_wide_MSG28_keeps_every_zone_unchanged_and_writes_no_audit()
    {
        await using var own = new WaterApiFactory();
        var a = await own.ZoneAsync();
        var b = await own.ZoneAsync();

        var response = await a.Owner.PutAsJsonAsync("/api/water-thresholds", new { phMin = 7m, phMax = 8m, salinityMin = 30m, salinityMax = 30m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG28", await response.ErrorCodeAsync());
        Assert.Equal("Salinity minimum", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        var zones = await own.QueryAsync(db => db.FarmingZones.AsNoTracking().ToListAsync());
        Assert.All(zones, zone => Assert.Equal((6.5m, 8.5m, 15m, 30m), (zone.PhMin!.Value, zone.PhMax!.Value, zone.SalinityMin!.Value, zone.SalinityMax!.Value)));
        Assert.Equal(0, await own.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "WaterThreshold.Updated")));
        Assert.NotEqual(a.Zone.Id, b.Zone.Id);
    }

    [Fact]
    public async Task Correction_of_a_correction_keeps_the_thresholds_of_the_first_confirmation()
    {
        var z = await api.ZoneAsync();
        var v1 = await z.RecordedAsync(8.0m, 20m);
        await SetPhAsync(z, 6.5m, 9.5m);
        var v2 = await (await z.Owner.CorrectAsync(v1.Id(), 8.4m, 20m, recordVersion: 1)).Content.ReadFromJsonAsync<JsonElement>();
        await SetPhAsync(z, 6.5m, 8.0m);

        var v3Response = await z.Owner.CorrectAsync(v2.Id(), 8.6m, 20m, recordVersion: 2);
        var v3 = await v3Response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, v3Response.StatusCode);
        Assert.Equal("None", v2.Text("alertState"));
        Assert.Equal(8.5m, v2.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Equal(3, v3.GetProperty("recordVersion").GetInt32());
        Assert.Equal("Active", v3.Text("alertState"));
        Assert.Equal("pH 8.60 > 8.50", v3.Text("violatedValues"));
        Assert.Equal(8.5m, v3.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Single(await api.AlertsOfAsync(v3.Id()), n => n.RecipientAccountId == z.OwnerAccount.Id);
    }

    private static async Task SetPhAsync(WaterZone z, decimal min, decimal max)
    {
        var response = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = min, phMax = max, salinityMin = 15m, salinityMax = 30m });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ConfirmAsync(WaterZone z, Guid id)
    {
        var response = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{id}/confirm", new { });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> FieldAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field");
    }
}
