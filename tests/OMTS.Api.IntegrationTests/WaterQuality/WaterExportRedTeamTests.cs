using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// ENV-03 history, chart and Excel export limits (QA red team): one zone per file, text is never a formula,
/// drafts never and corrected versions only as rows (never in the monthly summary), paging and day-range bounds.
/// </summary>
public class WaterExportRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private const int MeasurementPointColumn = 2;
    private const int PhColumn = 3;
    private const int MeasuredByColumn = 9;
    private const int SummaryPhMaximumColumn = 4;
    private const int SummaryPhCountColumn = 5;

    [Fact]
    public async Task Export_of_a_zone_never_contains_another_zones_measurements()
    {
        var zoneA = await api.ZoneAsync();
        var zoneB = zoneA with { Zone = await api.AddZoneAsync(NewCode()) };
        await zoneA.RecordedAsync(7.1m, 20m);
        await zoneB.RecordedAsync(7.9m, 21m);

        var export = await zoneA.Owner.GetAsync($"{zoneA.Base}/export");
        using var workbook = await WorkbookAsync(export);

        var phValues = workbook.Worksheet("Measurements").RowsUsed().Skip(1).Select(r => r.Cell(PhColumn).GetValue<decimal>()).ToList();
        Assert.Equal([7.1m], phValues);
        var summaryCount = workbook.Worksheet("Monthly summary").RowsUsed().Skip(1).Sum(r => r.Cell(SummaryPhCountColumn).GetValue<int>());
        Assert.Equal(1, summaryCount);
    }

    [Fact]
    public async Task Text_starting_with_an_equals_sign_is_written_as_text_never_as_a_formula()
    {
        var z = await api.ZoneAsync();
        const string point = "=HYPERLINK(\"http://evil.example\",\"Click\")";
        const string name = "=1+2";
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.WaterMeasurementPoint = point);
        var manager = await StaffNamedAsync(z.Zone, name);
        await z.RecordedAsync(7.3m, 20m, client: manager);

        var export = await z.Owner.GetAsync($"{z.Base}/export");
        using var workbook = await WorkbookAsync(export);

        var row = workbook.Worksheet("Measurements").Row(2);
        foreach (var (column, text) in new[] { (MeasurementPointColumn, point), (MeasuredByColumn, name) })
        {
            var cell = row.Cell(column);
            Assert.False(cell.HasFormula, $"column {column} is a formula");
            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.Equal(text, cell.GetString());
        }
    }

    [Fact]
    public async Task Drafts_are_never_exported_and_corrected_rows_never_reach_the_monthly_summary()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(13m, 22m, confirm: false);
        var original = await z.RecordedAsync(9.0m, 20m);
        Assert.Equal(HttpStatusCode.Created, (await z.Owner.CorrectAsync(original.Id(), 7.2m, 20m, recordVersion: 1)).StatusCode);

        using var drafts = await WorkbookAsync(await z.Owner.GetAsync($"{z.Base}/export?status=Draft"));
        using var corrected = await WorkbookAsync(await z.Owner.GetAsync($"{z.Base}/export?status=Corrected"));

        Assert.Single(drafts.Worksheet("Measurements").RowsUsed());
        var rows = corrected.Worksheet("Measurements").RowsUsed().Skip(1).ToList();
        Assert.Equal([9.0m], rows.Select(r => r.Cell(PhColumn).GetValue<decimal>()));
        var months = corrected.Worksheet("Monthly summary").RowsUsed().Skip(1).ToList();
        Assert.Equal(1, months.Sum(r => r.Cell(SummaryPhCountColumn).GetValue<int>()));
        Assert.Equal(7.2m, months.Single(r => r.Cell(SummaryPhCountColumn).GetValue<int>() == 1).Cell(SummaryPhMaximumColumn).GetValue<decimal>());
    }

    [Fact]
    public async Task History_from_and_to_are_whole_local_days_in_Asia_Ho_Chi_Minh()
    {
        var z = await api.ZoneAsync();
        var local = TimeSpan.FromHours(7);
        // 09/09 23:59 and 11/09 00:00 local fall outside 10/09; both ends of 10/09 are inside.
        await z.RecordedAsync(7.0m, 20m, measuredAt: new DateTimeOffset(2026, 9, 9, 23, 59, 0, local));
        await z.RecordedAsync(7.1m, 20m, measuredAt: new DateTimeOffset(2026, 9, 10, 0, 0, 0, local));
        await z.RecordedAsync(7.2m, 20m, measuredAt: new DateTimeOffset(2026, 9, 10, 23, 59, 0, local));
        await z.RecordedAsync(7.3m, 20m, measuredAt: new DateTimeOffset(2026, 9, 11, 0, 0, 0, local));

        var day = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?from=2026-09-10&to=2026-09-10");

        var phValues = day.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("ph").GetDecimal()).ToList();
        Assert.Equal([7.2m, 7.1m], phValues);
    }

    [Fact]
    public async Task History_page_size_is_capped_at_100_and_the_page_starts_at_1()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m);
        await z.RecordedAsync(7.1m, 20m);

        var huge = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?pageSize=1000");
        var zero = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}?page=0&pageSize=0");

        Assert.Equal(100, huge.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, huge.GetProperty("items").GetArrayLength());
        Assert.Equal(1, zero.GetProperty("page").GetInt32());
        Assert.Equal(1, zero.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, zero.GetProperty("items").GetArrayLength());
        Assert.Equal(2, zero.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Day_chart_accepts_exactly_366_days_and_refuses_367()
    {
        var z = await api.ZoneAsync();

        var longest = await z.Owner.GetAsync($"{z.Base}/chart?from=2025-10-23&to=2026-10-23&groupBy=day");
        var tooLong = await z.Owner.GetAsync($"{z.Base}/chart?from=2025-10-22&to=2026-10-23&groupBy=DAY");

        Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
        Assert.Equal(366, (await longest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("periods").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("MSG01", await tooLong.ErrorCodeAsync());
        Assert.Equal("Group by", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    private static async Task<XLWorkbook> WorkbookAsync(HttpResponseMessage export)
    {
        Assert.True(export.StatusCode == HttpStatusCode.OK, await export.Content.ReadAsStringAsync());
        return new XLWorkbook(await export.Content.ReadAsStreamAsync());
    }

    /// <summary>A Zone Manager with View and Create on WATER_MEASUREMENTS in the zone, whose display name is <paramref name="displayName"/>.</summary>
    private async Task<HttpClient> StaffNamedAsync(FarmingZone zone, string displayName)
    {
        var role = new Role { Name = $"Water {NewCode()}" };
        role.Permissions.Add(new RolePermission { FunctionCode = PermissionFunctions.WaterMeasurements, CanView = true, CanCreate = true });
        await api.UseDbAsync(db => db.Roles.Add(role));
        var account = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.DisplayName = displayName);
        await api.GrantAsync(account, role, zone);
        return await api.LoginAsync(account.Username);
    }
}
