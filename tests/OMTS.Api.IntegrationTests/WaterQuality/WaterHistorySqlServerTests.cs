using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>On SQL Server: the WATER-03 list, chart, export and zone overview over a corrected and a voided measurement.</summary>
public class WaterHistorySqlServerTests
{
    [SqlServerFact]
    public async Task List_chart_export_and_overview_agree_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await OwnerAsync(api);
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var older = await seeded.RecordAsync(owner, 7m, 20m, temperature: 21m, measuredAt: now.AddDays(-9));
        var latest = await seeded.RecordAsync(owner, 9m, 20m, temperature: 22m, measuredAt: now.AddDays(-3));
        var voided = await seeded.RecordAsync(owner, 7.5m, 20m, temperature: 23m, measuredAt: now.AddDays(-1));
        Assert.Equal(HttpStatusCode.OK, (await owner.VoidAsync(voided, "Đo nhầm cọc")).StatusCode);
        var based = $"/api/zones/{seeded.Zone.Id}/water-measurements";

        var list = await owner.GetFromJsonAsync<JsonElement>(based);
        var chart = await owner.GetFromJsonAsync<JsonElement>($"{based}/chart");
        var empty = await owner.GetFromJsonAsync<JsonElement>($"{based}?from=2026-01-01&to=2026-01-02");
        var overview = await owner.GetFromJsonAsync<JsonElement>("/api/water-measurements/zones");
        using var workbook = new XLWorkbook(await (await owner.GetAsync($"{based}/export")).Content.ReadAsStreamAsync());

        Assert.Equal([voided, latest, older], list.GetProperty("items").EnumerateArray().Select(r => r.Id()));
        Assert.Equal("Đo nhầm cọc", list.GetProperty("items")[0].Text("voidReason"));
        Assert.Equal([older, latest], chart.GetProperty("points").EnumerateArray().Select(p => p.Id()));
        Assert.Equal([21m, 22m], chart.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("temperature").GetDecimal()));
        Assert.Equal("MSG-GEN-10", empty.Text("messageCode"));
        var rows = workbook.Worksheet("Measurements").RowsUsed().Skip(1).ToList();
        Assert.Equal(["Voided", "Active", "Active"], rows.Select(r => r.Cell(6).GetString()));
        Assert.Equal([6.5m, 6.5m, 6.5m], rows.Select(r => r.Cell(11).GetValue<decimal>()));
        var zone = overview.EnumerateArray().Single(r => r.GetProperty("zoneId").GetGuid() == seeded.Zone.Id);
        Assert.Equal(now.AddDays(-3), zone.GetProperty("latestMeasuredAt").GetDateTimeOffset());
        Assert.True(zone.GetProperty("hasWaterAlert").GetBoolean());
        Assert.Equal("PH", zone.GetProperty("valuesOutside")[0].Text("indicator"));
    }

    private static async Task<HttpClient> OwnerAsync(SqlServerApiFactory api)
    {
        var secret = NewPassword();
        var account = await api.AddAccountAsync($"o{NewCode()}", secret);
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, account.Username, secret);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
