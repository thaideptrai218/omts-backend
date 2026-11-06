using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of the WATER-03.3 file: the rows of the list and text that must stay text.</summary>
public class WaterHistoryQaTestsExport(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private const int MeasuredAtColumn = 1;
    private const int RaftColumn = 2;
    private const int TemperatureColumn = 5;
    private const int VoidReasonColumn = 7;
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);

    // More rows than a page, some with the same time: the file holds the pages of the list one after the other. Paging does not cut the file.
    [Theory]
    [InlineData("")]
    [InlineData("?from=2026-03-01&to=2026-03-20")]
    [InlineData("?from=2026-03-01&to=2026-08-01&status=Voided")]
    [InlineData("?from=2026-03-01&to=2026-08-01&status=Active")]
    public async Task The_file_holds_the_rows_of_the_list_in_its_order(string filter)
    {
        var z = await api.ZoneAsync();
        var start = new DateTimeOffset(2026, 3, 1, 8, 0, 0, Local);
        var recent = new DateTimeOffset(TimeProvider.System.Today().ToDateTime(TimeOnly.MinValue), Local).AddDays(-30);
        await api.UseDbAsync(db => db.WaterMeasurements.AddRange(Enumerable.Range(0, 240).Select(i => new WaterMeasurement
        {
            ZoneId = z.Zone.Id,
            RaftId = z.Raft.Id,
            // Half are in the default period; rows share a time in threes; a tenth are voided.
            MeasuredAt = (i % 2 == 0 ? start : recent).AddHours(i / 3),
            Ph = 7m,
            Salinity = 20m,
            Temperature = 10m + (i / 100m),
            PhotoUrl = Guid.NewGuid().ToString("N"),
            ClientOperationId = Guid.NewGuid(),
            StatusId = i % 10 == 0 ? OMTS.Domain.Constants.StatusIds.WaterMeasurement.Voided : OMTS.Domain.Constants.StatusIds.WaterMeasurement.Active,
            VoidReason = i % 10 == 0 ? "Sai cọc" : null,
        })));

        var listed = new List<(decimal Temperature, string? Reason)>();
        var separator = filter.Length == 0 ? "?" : "&";
        for (var page = 1; ; page++)
        {
            var body = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}{filter}{separator}pageSize=100&page={page}");
            var items = body.GetProperty("items").EnumerateArray().ToList();
            listed.AddRange(items.Select(r => (r.GetProperty("temperature").GetDecimal(), r.Text("voidReason"))));
            if (items.Count == 0)
            {
                break;
            }
        }

        using var workbook = await WorkbookAsync(z, $"{filter}{separator}pageSize=1&page=2");

        var rows = workbook.Worksheet("Measurements").RowsUsed().Skip(1).ToList();
        Assert.NotEmpty(listed);
        Assert.Equal(listed.Select(r => r.Temperature), rows.Select(r => r.Cell(TemperatureColumn).GetValue<decimal>()));
        Assert.Equal(listed.Select(r => r.Reason ?? string.Empty), rows.Select(r => r.Cell(VoidReasonColumn).GetString()));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(1,1)")]
    [InlineData("=HYPERLINK(\"http://evil.example\",\"Click\")")]
    [InlineData("1E5")]
    [InlineData("2026-01-01")]
    [InlineData("TRUE")]
    public async Task A_void_reason_or_raft_code_stays_text(string text)
    {
        var z = await api.ZoneAsync();
        await api.UpdateAsync<Raft>(z.Raft.Id, raft => raft.Code = text);
        var measured = await z.RecordedAsync(7m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(measured.Id(), text)).StatusCode);

        using var workbook = await WorkbookAsync(z, string.Empty);

        var row = workbook.Worksheet("Measurements").Row(2);
        foreach (var column in new[] { RaftColumn, VoidReasonColumn })
        {
            Assert.False(row.Cell(column).HasFormula);
            Assert.Equal(XLDataType.Text, row.Cell(column).DataType);
            Assert.Equal(text, row.Cell(column).GetString());
        }
    }

    // A control character cannot be written raw into the XML of the file; the export must still answer with a file that opens.
    [Fact]
    public async Task A_void_reason_with_a_control_character_gives_a_file_that_opens()
    {
        var z = await api.ZoneAsync();
        var measured = await z.RecordedAsync(7m, 20m);
        var voided = await z.Owner.VoidAsync(measured.Id(), "Sai\u0001cọc");
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        using var workbook = await WorkbookAsync(z, string.Empty);

        Assert.Equal("Sai\u0001cọc", workbook.Worksheet("Measurements").Row(2).Cell(VoidReasonColumn).GetString());
    }

    // The earliest time a measurement can have is 01/01/2000 00:00 in Vietnam: a real date in the file, not text.
    [Fact]
    public async Task The_earliest_measurement_time_is_a_date_in_the_file_and_an_earlier_one_is_refused()
    {
        var z = await api.ZoneAsync();
        var earliest = new DateTimeOffset(2000, 1, 1, 0, 0, 0, Local);
        await z.RecordedAsync(7m, 20m, measuredAt: earliest);
        Assert.Equal(HttpStatusCode.BadRequest, (await z.RecordAsync(7m, 20m, measuredAt: earliest.AddMinutes(-1))).StatusCode);

        using var workbook = await WorkbookAsync(z, "?from=2000-01-01&to=2000-01-01");

        var cell = workbook.Worksheet("Measurements").Row(2).Cell(MeasuredAtColumn);
        Assert.Equal(XLDataType.DateTime, cell.DataType);
        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0), cell.GetDateTime());
    }

    private static async Task<XLWorkbook> WorkbookAsync(WaterZone z, string query)
    {
        var export = await z.Owner.GetAsync($"{z.Base}/export{query}");
        Assert.True(export.StatusCode == HttpStatusCode.OK, await export.Content.ReadAsStringAsync());
        return new XLWorkbook(await export.Content.ReadAsStreamAsync());
    }
}
