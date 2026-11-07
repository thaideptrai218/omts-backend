using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Reports;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>WATER-03.3: the measurements shown, each with its status (voided ones with their reason) and the thresholds saved with it.</summary>
public class WaterHistoryExportTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private const int PhColumn = 3;
    private const int StatusColumn = 6;
    private const int VoidReasonColumn = 7;
    private const int AlertColumn = 8;
    private const int PhMinColumn = 11;
    private const int SalinityMinColumn = 13;
    private const int TemperatureMaxColumn = 16;

    [Fact]
    public async Task Voided_rows_come_with_their_reason_in_the_order_of_the_list_and_each_row_with_its_own_thresholds()
    {
        var z = await api.ZoneAsync();
        var now = DateTimeOffset.UtcNow;
        await z.RecordedAsync(7.1m, 12m, measuredAt: now.AddDays(-9));
        // AC-04: the minimum goes from 15 to 10 ‰; the temperature thresholds are cleared.
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 6m, phMax = 9m, salinityMin = 10m, salinityMax = 30m })).StatusCode);
        var voided = await z.RecordedAsync(7.2m, 12m, measuredAt: now.AddDays(-5));
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(voided.Id(), "Đo nhầm cọc")).StatusCode);
        await z.RecordedAsync(7.3m, 12m, measuredAt: now.AddDays(-1));

        using var workbook = await WorkbookAsync(z, string.Empty);

        var rows = workbook.Worksheet("Measurements").RowsUsed().Skip(1).ToList();
        Assert.Equal([7.3m, 7.2m, 7.1m], rows.Select(r => r.Cell(PhColumn).GetValue<decimal>()));
        Assert.Equal(["Active", "Voided", "Active"], rows.Select(r => r.Cell(StatusColumn).GetString()));
        Assert.Equal(["", "Đo nhầm cọc", ""], rows.Select(r => r.Cell(VoidReasonColumn).GetString()));
        // The oldest keeps 15 ‰ and stays outside; the later ones saved 10 ‰ and are within.
        Assert.Equal([10m, 10m, 15m], rows.Select(r => r.Cell(SalinityMinColumn).GetValue<decimal>()));
        Assert.Equal(["", "", "Active"], rows.Select(r => r.Cell(AlertColumn).GetString()));
        Assert.Equal(6.5m, rows[2].Cell(PhMinColumn).GetValue<decimal>());
        Assert.Equal(6m, rows[0].Cell(PhMinColumn).GetValue<decimal>());
        Assert.Equal(35m, rows[2].Cell(TemperatureMaxColumn).GetValue<decimal>());
        Assert.True(rows[0].Cell(TemperatureMaxColumn).IsEmpty());
    }

    [Fact]
    public async Task The_file_has_the_headers_of_the_measurements_and_nothing_else()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m);

        var export = await z.Owner.GetAsync($"{z.Base}/export?from=2026-01-01&to=2026-03-31");
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(ISpreadsheetWriter.ContentType, export.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"water-{z.Zone.Code}-2026-01-01-2026-03-31.xlsx", export.Content.Headers.ContentDisposition!.FileNameStar ?? export.Content.Headers.ContentDisposition.FileName);
        var sheet = Assert.Single(workbook.Worksheets);
        Assert.Equal(
            ["Measured at", "Raft", "pH", "Salinity (‰)", "Temperature (°C)", "Status", "Void reason", "Alert", "Violated values", "Measured by",
             "pH minimum", "pH maximum", "Salinity minimum (‰)", "Salinity maximum (‰)", "Temperature minimum (°C)", "Temperature maximum (°C)"],
            sheet.Row(1).CellsUsed().Select(c => c.GetString()));
        // Nothing in the period: only the headers. The screen says MSG-GEN-10 from the list.
        Assert.Single(sheet.RowsUsed());
    }

    [Theory]
    [InlineData("?status=Active", new[] { "Active" })]
    [InlineData("?status=Voided", new[] { "Voided" })]
    [InlineData("?status=All", new[] { "Active", "Voided" })]
    public async Task The_status_filter_of_the_list_applies(string query, string[] statuses)
    {
        var z = await api.ZoneAsync();
        var voided = await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-2));
        await z.RecordedAsync(7.1m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(voided.Id())).StatusCode);

        using var workbook = await WorkbookAsync(z, query);

        Assert.Equal(statuses, workbook.Worksheet("Measurements").RowsUsed().Skip(1).Select(r => r.Cell(StatusColumn).GetString()));
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

    private static async Task<XLWorkbook> WorkbookAsync(WaterZone z, string query)
    {
        var export = await z.Owner.GetAsync($"{z.Base}/export{query}");
        Assert.True(export.StatusCode == HttpStatusCode.OK, await export.Content.ReadAsStringAsync());
        return new XLWorkbook(await export.Content.ReadAsStreamAsync());
    }

    private sealed class ThrowingSpreadsheetWriter : ISpreadsheetWriter
    {
        public byte[] Write(IReadOnlyList<SpreadsheetSheet> sheets) => throw new IOException("Disk full");
    }
}
