using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>ENV-03 – history list, detail with the version chain, chart and Excel export use only current confirmed values.</summary>
public class WaterHistoryTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);

    [Fact]
    public async Task Seeded_corrected_and_voided_measurements_leave_only_current_confirmed_values_in_chart_and_export()
    {
        var (z, ids) = await SeedAsync();
        var (from, to) = (DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-2)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)));

        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        var counted = chart.GetProperty("periods").EnumerateArray().Sum(p => p.GetProperty("ph").GetProperty("count").GetInt32());
        var phValues = chart.GetProperty("periods").EnumerateArray().Where(p => !p.GetProperty("missing").GetBoolean())
            .Select(p => p.GetProperty("ph").GetProperty("maximum").GetDecimal()).ToList();

        // Current: corrected version 7.2 and the untouched 7.6; not the corrected 9.0, the voided 5.0 or the draft 13.0.
        Assert.Equal(2, counted);
        Assert.DoesNotContain(9.0m, phValues);
        Assert.Equal(8.5m, chart.GetProperty("thresholds").GetProperty("phMax").GetDecimal());

        var export = await z.Owner.GetAsync($"{z.Base}/export?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        var rows = workbook.Worksheet("Measurements").RowsUsed().Skip(1).Select(r => r.Cell(3).GetValue<decimal>()).ToList();
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(ISpreadsheetWriter.ContentType, export.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"water-{z.Zone.Code}-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx", export.Content.Headers.ContentDisposition!.FileNameStar ?? export.Content.Headers.ContentDisposition.FileName);
        Assert.Equal([7.6m, 7.2m], rows.Order().Reverse());
        Assert.True(workbook.TryGetWorksheet("Monthly summary", out _));

        var detail = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{ids.Corrected}");
        Assert.Equal([9.0m, 7.2m], detail.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("ph").GetDecimal()));
        Assert.Equal(["Corrected", "Confirmed"], detail.GetProperty("versions").EnumerateArray().Select(v => v.Text("status")));
    }

    [Fact]
    public async Task History_defaults_to_confirmed_newest_first_and_shows_other_statuses_when_asked()
    {
        var (z, _) = await SeedAsync();

        var confirmed = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);
        var all = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?status=Draft,Confirmed&status=Corrected&status=Voided");
        var page = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?status=All&pageSize=2&page=2");

        var rows = confirmed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, confirmed.GetProperty("total").GetInt32());
        Assert.True(rows[0].GetProperty("measuredAt").GetDateTimeOffset() >= rows[1].GetProperty("measuredAt").GetDateTimeOffset());
        Assert.All(rows, r => Assert.Equal("Confirmed", r.Text("status")));
        Assert.Equal("Cọc đo số 1", rows[0].Text("measurementPoint"));
        Assert.Equal(5, all.GetProperty("total").GetInt32());
        Assert.Equal(["Confirmed", "Corrected", "Draft", "Voided"], all.GetProperty("items").EnumerateArray().Select(r => r.Text("status")).Distinct().Order());
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(5, page.GetProperty("total").GetInt32());
        var voided = all.GetProperty("items").EnumerateArray().Single(r => r.Text("status") == "Voided");
        Assert.Equal("Voided", voided.Text("alertState"));
        Assert.Equal(20, confirmed.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Chart_flags_months_without_data_and_groups_by_local_day()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m, measuredAt: new DateTimeOffset(2026, 8, 10, 8, 0, 0, Local));
        // 30/09 18:00 UTC is 01/10 01:00 local.
        await z.RecordedAsync(8m, 22m, measuredAt: new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero));

        var months = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart?from=2026-07-01&to=2026-10-15");
        var days = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart?from=2026-09-30&to=2026-10-01&groupBy=day");

        var periods = months.GetProperty("periods").EnumerateArray().ToList();
        Assert.Equal(["2026-07", "2026-08", "2026-09", "2026-10"], periods.Select(p => p.Text("period")));
        Assert.Equal([true, false, true, false], periods.Select(p => p.GetProperty("missing").GetBoolean()));
        Assert.Equal(7m, periods[1].GetProperty("ph").GetProperty("average").GetDecimal());
        Assert.Equal([true, false], days.GetProperty("periods").EnumerateArray().Select(p => p.GetProperty("missing").GetBoolean()));
    }

    [Fact]
    public async Task Default_chart_covers_twelve_whole_months_all_missing_when_there_is_no_data()
    {
        var z = await api.ZoneAsync();

        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart");

        Assert.Equal("Month", chart.Text("groupBy"));
        Assert.Equal(12, chart.GetProperty("periods").GetArrayLength());
        Assert.All(chart.GetProperty("periods").EnumerateArray(), p => Assert.True(p.GetProperty("missing").GetBoolean()));
    }

    [Theory]
    [InlineData("chart?from=2026-10-02&to=2026-10-01", "From")]
    [InlineData("chart?from=2025-01-01&to=2026-10-01&groupBy=day", "Group by")]
    [InlineData("chart?groupBy=week", "Group by")]
    [InlineData("?from=2026-13-01", "From")]
    [InlineData("?status=Deleted", "Status")]
    [InlineData("export?from=2026-10-05&to=2026-10-01", "From")]
    // Extreme dates used to overflow DateTimeOffset (500); the month chart is capped at 10 years.
    [InlineData("?from=0001-01-01", "From")]
    [InlineData("?to=9999-12-31", "To")]
    [InlineData("chart?from=0001-01-01", "From")]
    [InlineData("chart?to=9999-12-31&groupBy=day", "To")]
    [InlineData("export?from=1999-12-31", "From")]
    [InlineData("chart?from=2000-01-01&to=2026-10-01&groupBy=month", "Group by")]
    public async Task Invalid_filters_are_MSG01(string query, string field)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.GetAsync($"{z.Base}{(query.StartsWith('?') ? "" : "/")}{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Fact]
    public async Task A_ten_year_month_chart_and_the_earliest_date_are_still_accepted()
    {
        var z = await api.ZoneAsync();
        // The business date (UTC+7), as the API counts the ten years: the UTC date is a day behind it between
        // 00:00 and 07:00 Vietnam time.
        var from = TimeProvider.System.Today().AddYears(-10);

        var chart = await z.Owner.GetAsync($"{z.Base}/chart?from={from:yyyy-MM-dd}&groupBy=month");
        var list = await z.Owner.GetAsync($"{z.Base}?from=2000-01-01");

        Assert.Equal(HttpStatusCode.OK, chart.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(21474837)]
    public async Task A_huge_page_is_clamped_instead_of_overflowing_the_offset(int page)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.GetAsync($"{z.Base}?status=All&page={page}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("page").GetInt32() <= int.MaxValue / 100);
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Export_without_measurements_has_only_the_headers()
    {
        var z = await api.ZoneAsync();

        var export = await z.Owner.GetAsync($"{z.Base}/export?from=2026-01-01&to=2026-03-31");
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());

        var sheet = workbook.Worksheet("Measurements");
        Assert.Single(sheet.RowsUsed());
        Assert.Equal("Measured at", sheet.Cell(1, 1).GetString());
        var summary = workbook.Worksheet("Monthly summary");
        Assert.Equal(["2026-01", "2026-02", "2026-03"], summary.RowsUsed().Skip(1).Select(r => r.Cell(1).GetString()));
        Assert.All(summary.RowsUsed().Skip(1), r => Assert.True(r.Cell(10).GetBoolean()));
    }

    [Fact]
    public async Task Export_needs_the_export_permission_and_never_contains_drafts_or_voided_records()
    {
        var (z, _) = await SeedAsync();
        var (viewer, _) = await api.StaffAsync(z.Zone);
        var (exporter, _) = await api.StaffAsync(z.Zone, export: true);

        var refused = await viewer.GetAsync($"{z.Base}/export");
        var allowed = await exporter.GetAsync($"{z.Base}/export?status=All");
        using var workbook = new XLWorkbook(await allowed.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var statuses = workbook.Worksheet("Measurements").RowsUsed().Skip(1).Select(r => r.Cell(5).GetString()).ToList();
        Assert.Equal(["Confirmed", "Confirmed", "Corrected"], statuses.Order());
    }

    [Fact]
    public async Task File_generation_failure_is_500_MSG05()
    {
        await using var broken = new WaterApiFactory(services: s => s.AddSingleton<ISpreadsheetWriter, ThrowingSpreadsheetWriter>());
        var z = await broken.ZoneAsync();

        var response = await z.Owner.GetAsync($"{z.Base}/export");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("MSG05", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Zone_not_granted_is_403_for_history_chart_and_detail()
    {
        var (z, ids) = await SeedAsync();
        var (outsider, _) = await api.StaffAsync((await api.ZoneAsync()).Zone, export: true);
        var (viewer, _) = await api.StaffAsync(z.Zone);

        foreach (var url in new[] { z.Base, $"{z.Base}/chart", $"{z.Base}/export", $"/api/water-measurements/{ids.Corrected}" })
        {
            var response = await outsider.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/water-measurements/{ids.Corrected}")).StatusCode);
    }

    [Fact]
    public async Task Detail_has_the_ocr_draft_thresholds_used_and_a_short_lived_photo_url()
    {
        var z = await api.ZoneAsync();
        var draftId = (await (await z.RecognizeAsync("ph7.7_sal23")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measurement").Id();
        await z.Owner.PostAsJsonAsync($"/api/water-measurements/{draftId}/confirm", new { ph = 7.5m });

        var detail = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{draftId}");
        var photo = await z.Owner.GetAsync(detail.Text("photoUrl"));

        Assert.Equal(7.7m, detail.GetProperty("ocrDraft").GetProperty("ph").GetDecimal());
        Assert.Equal(7.5m, detail.GetProperty("ph").GetDecimal());
        Assert.Equal(6.5m, detail.GetProperty("thresholdsUsed").GetProperty("phMin").GetDecimal());
        Assert.Equal(z.Zone.Code, detail.Text("zoneCode"));
        Assert.True(detail.GetProperty("isLatestVersion").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal(WaterTestKit.Photo("ph7.7_sal23"), await photo.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// A zone with: one measurement corrected (9.0 → 7.2), one untouched (7.6), one voided (5.0) and one draft (13.0),
    /// all measured within the last weeks.
    /// </summary>
    private async Task<(WaterZone Zone, (Guid Corrected, Guid Voided) Ids)> SeedAsync()
    {
        var z = await api.ZoneAsync();
        var now = DateTimeOffset.UtcNow;
        var toCorrect = await z.RecordedAsync(9.0m, 20m, measuredAt: now.AddDays(-20));
        await z.RecordedAsync(7.6m, 21m, measuredAt: now.AddDays(-10));
        var toVoid = await z.RecordedAsync(5.0m, 22m, measuredAt: now.AddDays(-5));
        await z.RecordedAsync(13.0m, 22m, confirm: false, measuredAt: now.AddDays(-1));
        Assert.Equal(HttpStatusCode.Created, (await z.Owner.CorrectAsync(toCorrect.Id(), 7.2m, 20m, 1, measuredAt: now.AddDays(-20))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(toVoid.Id(), 1)).StatusCode);
        return (z, (toCorrect.Id(), toVoid.Id()));
    }

    private sealed class ThrowingSpreadsheetWriter : ISpreadsheetWriter
    {
        public byte[] Write(IReadOnlyList<SpreadsheetSheet> sheets) => throw new IOException("Disk full");
    }
}
