using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>WATER-03 chart: raw points of the active measurements with pH, salinity and temperature; aggregates stay on the client.</summary>
public class WaterHistoryChartTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);

    [Fact]
    public async Task The_chart_has_one_point_per_active_measurement_oldest_first_with_the_temperature()
    {
        var z = await api.ZoneAsync();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var corrected = await z.RecordedAsync(9.0m, 20m, measuredAt: now.AddDays(-20), temperature: 21m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.CorrectAsync(corrected.Id(), 7.2m, 19m, measuredAt: now.AddDays(-19), temperature: 22m)).StatusCode);
        var voided = await z.RecordedAsync(5.0m, 22m, measuredAt: now.AddDays(-5));
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(voided.Id())).StatusCode);
        var untouched = await z.RecordedAsync(7.6m, 21m, measuredAt: now.AddDays(-10), temperature: 33.5m);

        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart");

        var points = chart.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([corrected.Id(), untouched.Id()], points.Select(p => p.Id()));
        Assert.Equal(
            (7.2m, 19m, 22m, now.AddDays(-19)),
            (points[0].GetProperty("ph").GetDecimal(), points[0].GetProperty("salinity").GetDecimal(), points[0].GetProperty("temperature").GetDecimal(), points[0].GetProperty("measuredAt").GetDateTimeOffset()));
        Assert.Equal(33.5m, points[1].GetProperty("temperature").GetDecimal());
        Assert.False(chart.TryGetProperty("periods", out _));
        Assert.False(chart.TryGetProperty("groupBy", out _));
    }

    [Fact]
    public async Task Each_measurement_of_a_day_is_its_own_point_and_the_period_counts_by_vietnam_dates()
    {
        var z = await api.ZoneAsync();
        // 30/09 18:00 UTC is 01/10 01:00 in Vietnam.
        await z.RecordedAsync(8m, 22m, measuredAt: new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero));
        await z.RecordedAsync(7.5m, 22m, measuredAt: new DateTimeOffset(2026, 10, 1, 9, 0, 0, Local));
        await z.RecordedAsync(7m, 20m, measuredAt: new DateTimeOffset(2026, 9, 30, 23, 59, 0, Local));

        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart?from=2026-10-01&to=2026-10-01");

        Assert.Equal([8m, 7.5m], chart.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("ph").GetDecimal()));
        Assert.Equal(("2026-10-01", "2026-10-01"), (chart.Text("from"), chart.Text("to")));
    }

    [Fact]
    public async Task The_bands_are_the_current_thresholds_of_the_zone_with_the_temperature()
    {
        var z = await api.ZoneAsync();

        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart");

        var thresholds = chart.GetProperty("thresholds");
        Assert.Equal((8.5m, 35m), (thresholds.GetProperty("phMax").GetDecimal(), thresholds.GetProperty("temperatureMax").GetDecimal()));
    }

    // A client that still sends groupBy gets the points: the parameter is ignored, like other removed fields.
    [Theory]
    [InlineData("?groupBy=day")]
    [InlineData("?groupBy=week")]
    public async Task An_old_group_by_is_ignored(string query)
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m);

        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart{query}");

        Assert.Equal(1, chart.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task The_chart_defaults_to_the_period_of_the_list()
    {
        var z = await api.ZoneAsync();

        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart");
        var list = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);

        Assert.Equal((list.Text("from"), list.Text("to")), (chart.Text("from"), chart.Text("to")));
        Assert.Equal(0, chart.GetProperty("points").GetArrayLength());
    }
}
