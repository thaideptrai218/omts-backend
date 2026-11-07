using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of the WATER-03 list, chart and zone overview.</summary>
public class WaterHistoryQaTestsList(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private const string Overview = "/api/water-measurements/zones";
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);
    private static readonly DateOnly Today = TimeProvider.System.Today();

    private static DateTimeOffset At(DateOnly date, int hour, int minute) => new(date.ToDateTime(new TimeOnly(hour, minute)), Local);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_default_period_of_the_list_and_the_chart_starts_at_vietnam_midnight_three_months_back()
    {
        var z = await api.ZoneAsync();
        var first = Today.AddMonths(-3);
        await z.RecordedAsync(7.1m, 20m, measuredAt: At(first.AddDays(-1), 23, 59));
        var inside = await z.RecordedAsync(7.2m, 20m, measuredAt: At(first, 0, 0));

        var list = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);
        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart");

        Assert.Equal((Iso(first), Iso(Today)), (list.Text("from"), list.Text("to")));
        Assert.Equal([inside.Id()], list.GetProperty("items").EnumerateArray().Select(r => r.Id()));
        Assert.Equal([inside.Id()], chart.GetProperty("points").EnumerateArray().Select(r => r.Id()));
    }

    [Fact]
    public async Task Only_to_counts_three_months_back_from_it_and_only_from_runs_to_today()
    {
        var z = await api.ZoneAsync();

        var onlyTo = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?to=2026-05-31");
        var onlyFrom = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?from=2026-01-01");

        Assert.Equal(("2026-02-28", "2026-05-31"), (onlyTo.Text("from"), onlyTo.Text("to")));
        Assert.Equal(("2026-01-01", Iso(Today)), (onlyFrom.Text("from"), onlyFrom.Text("to")));
    }

    // WATER-03.4: a status parameter sent empty is no filter, so MSG-GEN-10 does not name a status and the export answers too.
    [Theory]
    [InlineData("&status=")]
    [InlineData("&status=,")]
    [InlineData("&status=%20,%20")]
    public async Task An_empty_status_is_no_filter_so_the_message_does_not_name_a_status(string status)
    {
        var z = await api.ZoneAsync();

        var list = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?from=2026-01-01&to=2026-01-02{status}");
        var export = await z.Owner.GetAsync($"{z.Base}/export?from=2026-01-01&to=2026-01-02{status}");

        Assert.Equal("There is no data for the zone and period you chose. Please choose again.", list.Text("message"));
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    [Fact]
    public async Task An_empty_status_next_to_a_named_one_keeps_the_filter_and_the_message_names_it()
    {
        var z = await api.ZoneAsync();

        var list = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?from=2026-01-01&to=2026-01-02&status=&status=Voided");

        Assert.Equal("There is no data for the zone, period and status you chose. Please choose again.", list.Text("message"));
    }

    [Fact]
    public async Task A_zone_with_only_voided_measurements_says_no_data_for_active_and_lists_them_otherwise()
    {
        var z = await api.ZoneAsync();
        var only = await z.RecordedAsync(7m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(only.Id(), "  Sai cọc  ")).StatusCode);

        var active = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?status=Active");
        var both = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);

        Assert.Equal("MSG-GEN-10", active.Text("messageCode"));
        Assert.Equal(1, both.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, both.GetProperty("messageCode").ValueKind);
        Assert.Equal("Sai cọc", both.GetProperty("items")[0].Text("voidReason"));
    }

    // The same measurement time: pages hold each row once, in the order of the saves.
    [Fact]
    public async Task Rows_with_the_same_time_page_in_a_stable_order()
    {
        var z = await api.ZoneAsync();
        var at = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
        var saved = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            saved.Insert(0, (await z.RecordedAsync(7m + (i / 10m), 20m, measuredAt: at)).Id());
        }

        var ids = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var body = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?pageSize=2&page={page}");
            ids.AddRange(body.GetProperty("items").EnumerateArray().Select(r => r.Id()));
        }

        Assert.Equal(saved, ids);
    }

    [Fact]
    public async Task A_zone_without_thresholds_has_empty_thresholds_and_nothing_marked()
    {
        var z = await api.ZoneAsync(thresholds: false);
        await z.RecordedAsync(13m, 49m, temperature: 44m);

        var row = (await z.Owner.GetFromJsonAsync<JsonElement>(z.Base)).GetProperty("items")[0];

        Assert.Equal(0, row.GetProperty("valuesOutside").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("thresholdsUsed").GetProperty("phMin").ValueKind);
        Assert.Equal("None", row.Text("alertState"));
    }

    [Fact]
    public async Task A_correction_keeps_the_thresholds_of_the_measurement_and_marks_the_new_values()
    {
        var z = await api.ZoneAsync();
        var measured = await z.RecordedAsync(7m, 20m, temperature: 25m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 1m, phMax = 2m })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.CorrectAsync(measured.Id(), 7m, 20m, temperature: 40m)).StatusCode);

        var row = (await z.Owner.GetFromJsonAsync<JsonElement>(z.Base)).GetProperty("items")[0];
        Assert.Equal(6.5m, row.GetProperty("thresholdsUsed").GetProperty("phMin").GetDecimal());
        Assert.Equal("TEMPERATURE", row.GetProperty("valuesOutside")[0].Text("indicator"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("voidReason").ValueKind);
    }

    // Finding L-4: without the 366-day and 10-year limits one request reads every Active row of the zone, as far back as
    // 2000-01-01, with no cap and no paging. No number is agreed, so any limit passes: a refusal or at most 1000 points.
    [Fact(Skip = "Waiting for the PM: should the chart and export have a row cap, and what number? The SRS sets none; about 330 rows per zone in 27 years at monthly measuring.")]
    public async Task QaL4_a_very_long_chart_period_on_a_large_zone_is_limited()
    {
        var z = await api.ZoneAsync();
        var start = new DateTimeOffset(2005, 1, 1, 8, 0, 0, Local);
        await api.UseDbAsync(db => db.WaterMeasurements.AddRange(Enumerable.Range(0, 3000).Select(i => new WaterMeasurement
        {
            ZoneId = z.Zone.Id,
            RaftId = z.Raft.Id,
            MeasuredAt = start.AddDays(i * 2),
            Ph = 7m,
            Salinity = 20m,
            Temperature = 25m,
            PhotoUrl = Guid.NewGuid().ToString("N"),
            ClientOperationId = Guid.NewGuid(),
        })));

        var response = await z.Owner.GetAsync($"{z.Base}/chart?from=2000-01-01&to={Iso(Today)}&groupBy=month");

        if (response.StatusCode != HttpStatusCode.BadRequest)
        {
            var points = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("points").GetArrayLength();
            Assert.True(points <= 1000, $"One chart request returned {points} points.");
        }
    }

    [Fact]
    public async Task The_overview_shows_the_alert_of_the_latest_active_when_the_voided_latest_was_within()
    {
        var z = await api.ZoneAsync();
        var earlier = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeSeconds());
        await z.RecordedAsync(9.5m, 20m, measuredAt: earlier);
        var latest = await z.RecordedAsync(7m, 20m, measuredAt: earlier.AddDays(2));
        Assert.False((await RowAsync(z)).GetProperty("hasWaterAlert").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(latest.Id())).StatusCode);

        var row = await RowAsync(z);
        Assert.Equal(earlier, row.GetProperty("latestMeasuredAt").GetDateTimeOffset());
        Assert.True(row.GetProperty("hasWaterAlert").GetBoolean());
        Assert.Equal("PH", row.GetProperty("valuesOutside")[0].Text("indicator"));
    }

    [Fact]
    public async Task A_void_of_the_only_measurement_clears_the_time_and_the_alert()
    {
        var z = await api.ZoneAsync();
        var only = await z.RecordedAsync(9.5m, 20m);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(only.Id())).StatusCode);

        var row = await RowAsync(z);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("latestMeasuredAt").ValueKind);
        Assert.False(row.GetProperty("hasWaterAlert").GetBoolean());
        Assert.Equal(0, row.GetProperty("valuesOutside").GetArrayLength());
    }

    [Fact]
    public async Task At_the_same_time_the_one_saved_later_decides_the_alert()
    {
        var z = await api.ZoneAsync();
        var at = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
        await z.RecordedAsync(9.5m, 20m, measuredAt: at);
        await z.RecordedAsync(7m, 20m, measuredAt: at);

        Assert.False((await RowAsync(z)).GetProperty("hasWaterAlert").GetBoolean());
    }

    [Fact]
    public async Task A_resumed_zone_has_its_alert_back()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(9.5m, 20m);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);
        Assert.False((await RowAsync(z)).GetProperty("hasWaterAlert").GetBoolean());

        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.InUse);

        Assert.True((await RowAsync(z)).GetProperty("hasWaterAlert").GetBoolean());
    }

    [Fact]
    public async Task A_malformed_in_use_only_is_refused_and_not_a_server_error()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.GetAsync($"{Overview}?inUseOnly=maybe");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<JsonElement> RowAsync(WaterZone z) =>
        (await z.Owner.GetFromJsonAsync<JsonElement>(Overview)).EnumerateArray().Single(r => r.GetProperty("zoneId").GetGuid() == z.Zone.Id);
}
