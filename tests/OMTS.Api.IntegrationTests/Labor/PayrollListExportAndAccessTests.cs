using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.PayrollTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>The payroll list, its filters, sort and summary, the .xlsx files, and the hard rule that only the Cooperative Owner uses any of it.</summary>
public class PayrollListExportAndAccessTests
{
    /// <summary>Four sheets: January (Finalized, half paid), February (Provisional), March (Cancelled) and a Custom one of April (Provisional).</summary>
    private sealed record Sheets(WorkSetup S, Guid An, JsonElement January, JsonElement February, JsonElement March, JsonElement April);

    private static async Task<Sheets> MakeSheetsAsync(AuthApiFactory factory)
    {
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", Dates("2024-01-02", "2024-01-03", "2024-02-05", "2024-03-05", "2024-04-05"));
        var january = await FinalizedAsync(s.Owner, await NewSheetAsync(s.Owner, 1, an));
        await PayAsync(s.Owner, january.GuidOf(), "Cash", (january.SlipOf(an).GuidOf(), 200_000));
        var february = await NewSheetAsync(s.Owner, 2, an);
        var march = await NewSheetAsync(s.Owner, 3, an);
        await s.Owner.PostAsync($"{Payrolls}/{march.GuidOf()}/cancel", null);
        var april = await (await CreateSheetAsync(s.Owner, new { periodType = "Custom", from = "2024-04-01", to = "2024-04-15", scope = "Selected", employeeIds = new[] { an } })).BodyAsync(HttpStatusCode.Created);
        return new Sheets(s, an, january, february, march, april);
    }

    private static string[] Codes(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.Str("name")!).ToArray();

    [Fact]
    public async Task The_list_filters_sorts_pages_and_sums_the_whole_filtered_set()
    {
        await using var factory = new AuthApiFactory();
        var m = await MakeSheetsAsync(factory);
        var owner = m.S.Owner;

        var all = await owner.JsonAsync($"{Payrolls}?status=Creating&status=Provisional&status=Finalized");
        Assert.Equal(["Bảng lương 01/04/2024 - 15/04/2024", "Bảng lương tháng 02/2024", "Bảng lương tháng 01/2024"], Codes(all));
        Assert.Equal((3, 1, 15), (all.GetProperty("total").GetInt32(), all.GetProperty("page").GetInt32(), all.GetProperty("pageSize").GetInt32()));
        Assert.Equal((1_280_000m, 200_000m, 1_080_000m), (all.GetProperty("summary").Money("total"), all.GetProperty("summary").Money("paid"), all.GetProperty("summary").Money("outstanding")));
        var january = all.GetProperty("items").EnumerateArray().Single(i => i.Str("status") == "Finalized");
        Assert.Equal((640_000m, 200_000m, 440_000m, "Monthly"), (january.Money("total"), january.Money("paid"), january.Money("outstanding"), january.Str("periodType")));
        Assert.Matches(@"^BL\d{6}$", january.Str("code"));

        Assert.Equal(4, (await owner.JsonAsync(Payrolls)).GetProperty("total").GetInt32());
        Assert.Single((await owner.JsonAsync($"{Payrolls}?status=Cancelled")).GetProperty("items").EnumerateArray());
        Assert.Equal(["Bảng lương tháng 01/2024"], Codes(await owner.JsonAsync($"{Payrolls}?status=Finalized")));
        Assert.Equal(["Bảng lương 01/04/2024 - 15/04/2024"], Codes(await owner.JsonAsync($"{Payrolls}?periodType=Custom")));
        Assert.Equal(["Bảng lương tháng 02/2024"], Codes(await owner.JsonAsync($"{Payrolls}?search=02%2F2024")));
        Assert.Equal(["Bảng lương tháng 01/2024"], Codes(await owner.JsonAsync($"{Payrolls}?search={january.Str("code")}")));
        Assert.Equal(["Bảng lương tháng 02/2024", "Bảng lương tháng 03/2024", "Bảng lương 01/04/2024 - 15/04/2024"],
            Codes(await owner.JsonAsync($"{Payrolls}?from=2024-02-10&to=2024-04-02&sortBy=from")));
        Assert.Equal(["Bảng lương tháng 01/2024", "Bảng lương tháng 02/2024", "Bảng lương tháng 03/2024", "Bảng lương 01/04/2024 - 15/04/2024"],
            Codes(await owner.JsonAsync($"{Payrolls}?sortBy=from&sortDir=asc")));

        var firstPage = await owner.JsonAsync($"{Payrolls}?pageSize=2&page=1&sortBy=total&sortDir=desc");
        var secondPage = await owner.JsonAsync($"{Payrolls}?pageSize=2&page=2&sortBy=total&sortDir=desc");
        Assert.Equal((4, 2, 2), (firstPage.GetProperty("total").GetInt32(), firstPage.GetProperty("items").GetArrayLength(), secondPage.GetProperty("items").GetArrayLength()));
        Assert.Equal(1_600_000m, firstPage.GetProperty("summary").Money("total"));
        Assert.Equal(640_000m, firstPage.GetProperty("items")[0].Money("total"));
    }

    [Theory]
    [InlineData("sortBy=weight")]
    [InlineData("sortDir=sideways")]
    [InlineData("from=2024-05-01&to=2024-04-01")]
    [InlineData("status=Lost")]
    [InlineData("periodType=Yearly")]
    public async Task A_filter_that_does_not_exist_is_400(string query)
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"{Payrolls}?{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"{Payrolls}/export?{query}")).StatusCode);
    }

    [Fact]
    public async Task The_payments_are_listed_by_search_and_period_and_an_employees_payslips_carry_their_summary()
    {
        await using var factory = new AuthApiFactory();
        var m = await MakeSheetsAsync(factory);
        var owner = m.S.Owner;
        var code = (await owner.CashLinesAsync("Cash", "CTM")).Single().Str("code")!;

        var byCode = await owner.JsonAsync($"{Payments}?search={code}");
        var byEmployee = await owner.JsonAsync($"{Payments}?search=An");
        var byPayroll = await owner.JsonAsync($"{Payments}?search={m.January.Str("code")}");
        var elsewhere = await owner.JsonAsync($"{Payments}?from=2020-01-01&to=2020-01-31");
        var inPeriod = await owner.JsonAsync($"{Payments}?from={Day(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)))}&to={Day(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)))}");

        var row = byCode.GetProperty("items").EnumerateArray().Single();
        Assert.Equal((code, "An", m.January.Str("code"), "Cash", 200_000m, "Wage", "Recorded"),
            (row.Str("voucherCode"), row.Str("employeeName"), row.Str("payrollCode"), row.Str("method"), row.Money("amount"), row.Str("kind"), row.Str("status")));
        Assert.Equal(m.January.GuidOf(), row.GuidOf("payrollId"));
        Assert.Equal(1, byEmployee.GetProperty("total").GetInt32());
        Assert.Equal(1, byPayroll.GetProperty("total").GetInt32());
        Assert.Equal(0, elsewhere.GetProperty("total").GetInt32());
        Assert.Equal(1, inPeriod.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"{Payments}?from=2024-02-01&to=2024-01-01")).StatusCode);

        var slips = await owner.JsonAsync($"/api/employees/{m.An}/payslips");
        Assert.Equal(["Provisional", "Provisional", "Finalized"], slips.GetProperty("items").EnumerateArray().Select(i => i.Str("status")!));
        Assert.Equal((440_000m, 0m), (slips.GetProperty("summary").Money("outstanding"), slips.GetProperty("summary").Money("advanceBalance")));
        var finalized = await owner.JsonAsync($"/api/employees/{m.An}/payslips?status=Finalized&pageSize=1");
        var item = finalized.GetProperty("items").EnumerateArray().Single();
        Assert.Equal((m.January.SlipOf(m.An).Str("code"), "Bảng lương tháng 01/2024", 640_000m, 200_000m, 440_000m, "2024-01-01", "2024-01-31"),
            (item.Str("code"), item.Str("payrollName"), item.Money("net"), item.Money("paid"), item.Money("outstanding"), item.Str("from"), item.Str("to")));
        Assert.Equal(1, (await owner.JsonAsync($"/api/employees/{m.An}/payslips?status=Cancelled")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/employees/{Guid.NewGuid()}/payslips")).StatusCode);

        var slip = await owner.JsonAsync($"/api/payroll-slips/{m.January.SlipOf(m.An).GuidOf()}");
        Assert.Equal((200_000m, 440_000m, "Finalized"), (slip.Money("paid"), slip.Money("outstanding"), slip.Str("status")));
        Assert.Equal(code, slip.GetProperty("payments")[0].Str("voucherCode"));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/payroll-slips/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_sheet_and_the_list_download_as_xlsx_with_a_plain_file_name_and_no_cache()
    {
        await using var factory = new AuthApiFactory();
        var m = await MakeSheetsAsync(factory);

        var sheetFile = await m.S.Owner.GetAsync($"{Payrolls}/{m.January.GuidOf()}/export");
        var listFile = await m.S.Owner.GetAsync($"{Payrolls}/export?status=Finalized&status=Provisional&sortBy=code&sortDir=asc");

        foreach (var file in new[] { sheetFile, listFile })
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.Content.Headers.ContentType?.MediaType);
            Assert.Contains("no-store", file.Headers.CacheControl?.ToString());
            Assert.Matches("^[A-Za-z0-9._-]+$", file.Content.Headers.ContentDisposition?.FileNameStar ?? file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }

        Assert.Equal($"payroll-{m.January.Str("code")}.xlsx", sheetFile.Content.Headers.ContentDisposition?.FileNameStar ?? sheetFile.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var book = new XLWorkbook(await sheetFile.Content.ReadAsStreamAsync());
        var sheet = book.Worksheet(1);
        Assert.Equal(["Mã phiếu lương", "Mã nhân viên", "Tên nhân viên"], Enumerable.Range(1, 3).Select(c => sheet.Cell(1, c).GetString()));
        Assert.Equal(("An", 16d, 640_000d, 200_000d, 440_000d), (sheet.Cell(2, 3).GetString(), sheet.Cell(2, 4).GetDouble(), sheet.Cell(2, 9).GetDouble(), sheet.Cell(2, 10).GetDouble(), sheet.Cell(2, 11).GetDouble()));
        Assert.Equal("Tổng", sheet.Cell(3, 1).GetString());
        using var list = new XLWorkbook(await listFile.Content.ReadAsStreamAsync());
        Assert.Equal(4, list.Worksheet(1).LastRowUsed()!.RowNumber());
        Assert.Equal("Đã chốt lương", list.Worksheet(1).Cell(2, 9).GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await m.S.Owner.GetAsync($"{Payrolls}/{Guid.NewGuid()}/export")).StatusCode);
    }

    [Fact]
    public async Task Only_the_cooperative_owner_uses_the_payroll_by_a_fixed_rule_whatever_roles_say()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var accountant = await StaffAsync(factory, StaffRole.Accountant);
        var zoneManager = s.Manager;
        var anonymous = factory.CreateClient();
        var id = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, Payrolls, null),
            (HttpMethod.Get, $"{Payrolls}/export", null),
            (HttpMethod.Get, $"{Payrolls}/{id}", null),
            (HttpMethod.Get, $"{Payrolls}/{id}/export", null),
            (HttpMethod.Get, $"{Payrolls}/payable-employees", null),
            (HttpMethod.Get, Payments, null),
            (HttpMethod.Get, $"/api/employees/{id}/payslips", null),
            (HttpMethod.Get, $"/api/payroll-slips/{id}", null),
            (HttpMethod.Post, Payrolls, MonthBody(3)),
            (HttpMethod.Post, $"{Payrolls}/{id}/reload", new { mode = "All" }),
            (HttpMethod.Post, $"{Payrolls}/{id}/finalize", new { expenseMonth = "2024-03" }),
            (HttpMethod.Post, $"{Payrolls}/{id}/cancel", null),
            (HttpMethod.Post, $"{Payrolls}/{id}/payments", new { paidAt = DateTimeOffset.UtcNow.ToString("o"), method = "Cash", note = "", lines = new[] { new { slipId = id, amount = 1 } } }),
            (HttpMethod.Post, Payments, new { employeeId = id, amount = 1, paidAt = DateTimeOffset.UtcNow.ToString("o"), method = "Cash", note = "" }),
        };

        foreach (var (method, url, body) in requests)
        {
            foreach (var (who, expected) in new[] { (accountant, HttpStatusCode.Forbidden), (zoneManager, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
            {
                var response = await who.SendAsync(new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) });
                Assert.True(response.StatusCode == expected, $"{method} {url}: expected {(int)expected}, got {(int)response.StatusCode}");
                if (expected == HttpStatusCode.Forbidden)
                {
                    Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
                }
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.GetAsync(Payrolls)).StatusCode);
        Assert.Equal(0, await factory.QueryAsync(db => db.Payrolls.CountAsync()));
    }
}
