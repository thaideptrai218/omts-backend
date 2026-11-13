using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.PayrollTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-14 and BR-LABOR-11: wage payments of a Finalized sheet, each with its cash voucher, and the payments outside a sheet.</summary>
public class PayrollPaymentTests
{
    /// <summary>A Finalized sheet of March 2024 with two workers: An (2 days, 640,000) and Bình (1 day, 320,000).</summary>
    private sealed record Finalized(WorkSetup S, Guid An, Guid Binh, JsonElement Sheet, Guid SlipAn, Guid SlipBinh);

    private static async Task<Finalized> FinalizedSheetAsync(AuthApiFactory factory)
    {
        var s = await SetupWorkAsync(factory);
        await factory.EnsureVoucherTypeAsync();
        var an = await WorkerAsync(s, "An", FirstDays(3, 2));
        var binh = await WorkerAsync(s, "Bình", FirstDays(3, 1));
        var sheet = await FinalizedAsync(s.Owner, await NewSheetAsync(s.Owner, 3));
        return new Finalized(s, an, binh, sheet, sheet.SlipOf(an).GuidOf(), sheet.SlipOf(binh).GuidOf());
    }

    [Fact]
    public async Task A_partial_payment_makes_a_cash_voucher_and_lowers_what_is_owed()
    {
        await using var factory = new AuthApiFactory();
        var f = await FinalizedSheetAsync(factory);

        var response = await PayAsync(f.S.Owner, f.Sheet.GuidOf(), "Cash", (f.SlipAn, 200_000));

        var result = await response.BodyAsync(HttpStatusCode.Created);
        var voucher = result.GetProperty("vouchers").EnumerateArray().Single();
        Assert.Equal((f.SlipAn, "An", 200_000m), (voucher.GuidOf("slipId"), voucher.Str("employeeName"), voucher.Money("amount")));
        Assert.Matches(@"^CTM\d{6}$", voucher.Str("code"));
        var sheet = await f.S.Owner.SheetAsync(f.Sheet.GuidOf());
        Assert.Equal((200_000m, 440_000m), (sheet.SlipOf(f.An).Money("paid"), sheet.SlipOf(f.An).Money("outstanding")));
        Assert.Equal((200_000m, 760_000m), (sheet.Money("paid"), sheet.Money("outstanding")));
        Assert.Equal(1, await factory.AuditAsync("WagePayment.Recorded"));

        var line = Assert.Single(await f.S.Owner.CashLinesAsync("Cash", voucher.Str("code")!));
        Assert.Equal(("Payment", "Chi trả lương NV", "An", 200_000m), (line.Str("direction"), line.GetProperty("type").Str("name"), line.GetProperty("party").Str("name"), line.Money("amount")));
        Assert.True(line.GetProperty("countsInResult").GetBoolean());
    }

    [Fact]
    public async Task Several_slips_and_a_transfer_make_one_voucher_each_in_the_bank_fund()
    {
        await using var factory = new AuthApiFactory();
        var f = await FinalizedSheetAsync(factory);

        var result = await (await PayAsync(f.S.Owner, f.Sheet.GuidOf(), "BankTransfer", (f.SlipAn, 640_000), (f.SlipBinh, 320_000))).BodyAsync(HttpStatusCode.Created);

        var codes = result.GetProperty("vouchers").EnumerateArray().Select(v => v.Str("code")!).ToList();
        Assert.Equal(2, codes.Count);
        Assert.All(codes, c => Assert.Matches(@"^CNH\d{6}$", c));
        Assert.Equal(2, (await f.S.Owner.CashLinesAsync("Bank", "CNH")).Length);
        var sheet = await f.S.Owner.SheetAsync(f.Sheet.GuidOf());
        Assert.Equal((960_000m, 0m), (sheet.Money("paid"), sheet.Money("outstanding")));
    }

    [Fact]
    public async Task A_payment_over_what_is_owed_or_not_valid_creates_nothing()
    {
        await using var factory = new AuthApiFactory();
        var f = await FinalizedSheetAsync(factory);
        var id = f.Sheet.GuidOf();
        await (await PayAsync(f.S.Owner, id, "Cash", (f.SlipAn, 600_000))).BodyAsync(HttpStatusCode.Created);

        var over = await PayAsync(f.S.Owner, id, "Cash", (f.SlipAn, 40_001));
        var zero = await PayAsync(f.S.Owner, id, "Cash", (f.SlipAn, 0));
        var fraction = await PayAsync(f.S.Owner, id, "Cash", (f.SlipAn, 10.5m));
        var twice = await PayAsync(f.S.Owner, id, "Cash", (f.SlipAn, 1_000), (f.SlipAn, 1_000));
        var foreign = await PayAsync(f.S.Owner, id, "Cash", (Guid.NewGuid(), 1_000));
        var other = await PayAsync(f.S.Owner, id, "Other", (f.SlipAn, 1_000));
        var future = await f.S.Owner.PostAsJsonAsync($"{Payrolls}/{id}/payments", new { paidAt = DateTimeOffset.UtcNow.AddDays(2).ToString("o"), method = "Cash", note = "", lines = new[] { new { slipId = f.SlipAn, amount = 1_000 } } });
        var empty = await f.S.Owner.PostAsJsonAsync($"{Payrolls}/{id}/payments", new { paidAt = DateTimeOffset.UtcNow.ToString("o"), method = "Cash", note = "", lines = Array.Empty<object>() });

        Assert.Equal((HttpStatusCode.BadRequest, "MSG-LABOR-34"), (over.StatusCode, await over.ErrorCodeAsync()));
        Assert.Contains("40,000", JsonDocument.Parse(await over.Content.ReadAsStringAsync()).RootElement.Detail());
        foreach (var refused in new[] { zero, fraction, twice, foreign, other, future, empty })
        {
            Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        }

        Assert.Equal(600_000m, await factory.PaidOnAsync(f.SlipAn));
        Assert.Single(await f.S.Owner.CashLinesAsync("Cash", "CTM"));
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(f.S.Owner, id, "Cash", (f.SlipAn, 40_000))).StatusCode);
    }

    [Fact]
    public async Task Only_a_finalized_sheet_is_paid_and_a_slip_of_another_sheet_is_refused()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", Dates("2024-03-01", "2024-04-01"));
        var provisional = await NewSheetAsync(s.Owner, 4, an);
        var finalized = await FinalizedAsync(s.Owner, await NewSheetAsync(s.Owner, 3, an));

        var early = await PayAsync(s.Owner, provisional.GuidOf(), "Cash", (provisional.SlipOf(an).GuidOf(), 1_000));
        var mixed = await PayAsync(s.Owner, finalized.GuidOf(), "Cash", (provisional.SlipOf(an).GuidOf(), 1_000));
        var missing = await PayAsync(s.Owner, Guid.NewGuid(), "Cash", (Guid.NewGuid(), 1_000));

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (mixed.StatusCode, await mixed.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_request_sent_again_with_the_same_key_returns_the_same_payments_and_creates_nothing()
    {
        await using var factory = new AuthApiFactory();
        var f = await FinalizedSheetAsync(factory);
        HttpRequestMessage Request() => new(HttpMethod.Post, $"{Payrolls}/{f.Sheet.GuidOf()}/payments")
        {
            Content = JsonContent.Create(new { paidAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"), method = "Cash", note = "", lines = new[] { new { slipId = f.SlipAn, amount = 100_000 } } }),
            Headers = { { "Idempotency-Key", "payroll-pay-0001" } },
        };

        var first = await (await f.S.Owner.SendAsync(Request())).BodyAsync(HttpStatusCode.Created);
        var again = await (await f.S.Owner.SendAsync(Request())).BodyAsync(HttpStatusCode.Created);

        Assert.Equal(first.GetProperty("vouchers")[0].Str("code"), again.GetProperty("vouchers")[0].Str("code"));
        Assert.Equal(100_000m, await factory.PaidOnAsync(f.SlipAn));
        Assert.Single(await f.S.Owner.CashLinesAsync("Cash", "CTM"));
    }

    [Fact]
    public async Task A_voucher_cancelled_in_the_cash_book_gives_the_money_back_to_what_is_owed()
    {
        await using var factory = new AuthApiFactory();
        var f = await FinalizedSheetAsync(factory);
        var result = await (await PayAsync(f.S.Owner, f.Sheet.GuidOf(), "Cash", (f.SlipAn, 640_000))).BodyAsync(HttpStatusCode.Created);

        var cancel = await f.S.Owner.PostAsJsonAsync($"/api/cash-book/vouchers/{result.GetProperty("vouchers")[0].GuidOf()}/cancel", new { reason = "Chi nhầm" });

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var slip = (await f.S.Owner.SheetAsync(f.Sheet.GuidOf())).SlipOf(f.An);
        Assert.Equal((0m, 640_000m), (slip.Money("paid"), slip.Money("outstanding")));
        var history = await f.S.Owner.JsonAsync($"{Payments}?employeeId={f.An}");
        Assert.Equal("Cancelled", history.GetProperty("items")[0].Str("status"));
    }

    [Fact]
    public async Task A_payment_outside_a_sheet_pays_the_oldest_slips_first_and_the_rest_is_an_advance()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", Dates("2024-03-01", "2024-03-02", "2024-04-01"));
        var march = await FinalizedAsync(s.Owner, await NewSheetAsync(s.Owner, 3, an));
        var april = await FinalizedAsync(s.Owner, await NewSheetAsync(s.Owner, 4, an));

        var created = await (await PayEmployeeAsync(s.Owner, an, 700_000)).BodyAsync(HttpStatusCode.Created);

        Assert.NotEqual(Guid.Empty, created.GuidOf());
        Assert.Equal((640_000m, 0m), (march.SlipOf(an).Money("net"), (await s.Owner.SheetAsync(march.GuidOf())).SlipOf(an).Money("outstanding")));
        Assert.Equal(260_000m, (await s.Owner.SheetAsync(april.GuidOf())).SlipOf(an).Money("outstanding"));
        var history = await s.Owner.JsonAsync($"{Payments}?employeeId={an}");
        Assert.Equal(["Wage", "Wage"], history.GetProperty("items").EnumerateArray().Select(i => i.Str("kind")));
        Assert.Equal([60_000m, 640_000m], history.GetProperty("items").EnumerateArray().Select(i => i.Money("amount")));
        Assert.Equal(created.GuidOf(), history.GetProperty("items").EnumerateArray().Single(i => i.Money("amount") == 640_000m).GuidOf());
        Assert.Equal(2, await factory.QueryAsync(db => db.CashVouchers.CountAsync(v => v.PartyId == an)));

        var advance = await (await PayEmployeeAsync(s.Owner, an, 360_000)).BodyAsync(HttpStatusCode.Created);
        var rows = (await s.Owner.JsonAsync($"{Payments}?employeeId={an}")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(("Advance", 100_000m, JsonValueKind.Null), (rows[0].Str("kind")!, rows[0].Money("amount"), rows[0].GetProperty("payrollId").ValueKind));
        Assert.Equal(("Wage", 260_000m), (rows[1].Str("kind")!, rows[1].Money("amount")));
        Assert.NotEqual(Guid.Empty, advance.GuidOf());
    }

    [Fact]
    public async Task An_advance_is_deducted_from_the_next_finalized_sheet_of_the_employee_once()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", Dates("2024-03-01", "2024-04-01", "2024-05-01"));
        var march = await FinalizedAsync(s.Owner, await NewSheetAsync(s.Owner, 3, an));
        await (await PayAsync(s.Owner, march.GuidOf(), "Cash", (march.SlipOf(an).GuidOf(), 320_000))).BodyAsync(HttpStatusCode.Created);
        await (await PayEmployeeAsync(s.Owner, an, 500_000)).BodyAsync(HttpStatusCode.Created);
        var summary = (await s.Owner.JsonAsync($"/api/employees/{an}/payslips?status=Finalized")).GetProperty("summary");
        Assert.Equal((0m, 500_000m), (summary.Money("outstanding"), summary.Money("advanceBalance")));

        var april = await NewSheetAsync(s.Owner, 4, an);
        Assert.Equal((320_000m, 320_000m, 0m), (april.SlipOf(an).Money("income"), april.SlipOf(an).Money("deduction"), april.SlipOf(an).Money("net")));
        var finalized = await FinalizedAsync(s.Owner, april);
        var slip = finalized.SlipOf(an);
        Assert.Equal((320_000m, 320_000m, 0m, 0m), (slip.Money("income"), slip.Money("deduction"), slip.Money("net"), slip.Money("outstanding")));

        summary = (await s.Owner.JsonAsync($"/api/employees/{an}/payslips?status=Finalized")).GetProperty("summary");
        Assert.Equal((0m, 180_000m), (summary.Money("outstanding"), summary.Money("advanceBalance")));
        var may = await FinalizedAsync(s.Owner, await NewSheetAsync(s.Owner, 5, an));
        Assert.Equal((180_000m, 140_000m), (may.SlipOf(an).Money("deduction"), may.SlipOf(an).Money("net")));
        Assert.Equal(0m, (await s.Owner.JsonAsync($"/api/employees/{an}/payslips")).GetProperty("summary").Money("advanceBalance"));
    }

    [Fact]
    public async Task A_payment_outside_a_sheet_needs_a_real_employee_and_an_advance_needs_an_employed_worker()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var worker = await AddEmployeeAsync(s, "Công nhân");
        var staff = await AddEmployeeAsync(s, "Nhân viên", staff: true);
        var left = await WorkerAsync(s, "Đã nghỉ", FirstDays(3, 1));
        await FinalizedAsync(s.Owner, await NewSheetAsync(s.Owner, 3, left));
        await factory.UpdateAsync<OMTS.Domain.Entities.Identity.Employee>(left, e => e.StatusId = OMTS.Domain.Constants.StatusIds.Employee.Left);

        Assert.Equal(HttpStatusCode.BadRequest, (await PayEmployeeAsync(s.Owner, Guid.NewGuid(), 1_000)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PayEmployeeAsync(s.Owner, worker, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PayEmployeeAsync(s.Owner, staff, 1_000)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PayEmployeeAsync(s.Owner, worker, 1_000)).StatusCode);
        // A Left employee is paid only what is owed on the payslips.
        var owed = await PayEmployeeAsync(s.Owner, left, 320_001);
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-LABOR-34"), (owed.StatusCode, await owed.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.Created, (await PayEmployeeAsync(s.Owner, left, 320_000)).StatusCode);
    }

    [Fact]
    public async Task The_payable_employees_are_those_owed_and_with_includeAll_every_employed_one()
    {
        await using var factory = new AuthApiFactory();
        var f = await FinalizedSheetAsync(factory);
        var idle = await AddEmployeeAsync(f.S, "Rảnh");
        await PayAsync(f.S.Owner, f.Sheet.GuidOf(), "Cash", (f.SlipBinh, 320_000));

        var owed = await f.S.Owner.JsonAsync($"{Payrolls}/payable-employees");
        var all = await f.S.Owner.JsonAsync($"{Payrolls}/payable-employees?includeAll=true");
        var searched = await f.S.Owner.JsonAsync($"{Payrolls}/payable-employees?includeAll=true&search=R%E1%BA%A3nh");

        Assert.Equal([("An", 640_000m)], owed.GetProperty("items").EnumerateArray().Select(i => (i.Str("name")!, i.Money("outstanding"))));
        Assert.Equal([("An", 640_000m), ("Bình", 0m), ("Rảnh", 0m)], all.GetProperty("items").EnumerateArray().Select(i => (i.Str("name")!, i.Money("outstanding"))));
        Assert.Equal([idle], searched.GetProperty("items").EnumerateArray().Select(i => i.GuidOf("employeeId")));
        Assert.Matches(@"^NV\d{6}$", all.GetProperty("items")[0].Str("code"));
    }

    [Fact]
    public async Task The_payments_list_filters_by_payrollId_and_names_the_sheet()
    {
        await using var factory = new AuthApiFactory();
        var f = await FinalizedSheetAsync(factory);
        await PayAsync(f.S.Owner, f.Sheet.GuidOf(), "Cash", (f.SlipAn, 640_000), (f.SlipBinh, 320_000));
        await PayEmployeeAsync(f.S.Owner, f.An, 5_000);

        var all = await f.S.Owner.JsonAsync(Payments);
        var ofSheet = await f.S.Owner.JsonAsync($"{Payments}?payrollId={f.Sheet.GuidOf()}&pageSize=1&page=2");
        var none = await f.S.Owner.JsonAsync($"{Payments}?payrollId={Guid.NewGuid()}");
        var withEmployee = await f.S.Owner.JsonAsync($"{Payments}?payrollId={f.Sheet.GuidOf()}&employeeId={f.Binh}");

        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal(2, ofSheet.GetProperty("total").GetInt32());
        Assert.Equal(f.Sheet.GuidOf(), ofSheet.GetProperty("items")[0].GuidOf("payrollId"));
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Equal(1, withEmployee.GetProperty("total").GetInt32());
        Assert.Equal("Bảng lương tháng 03/2024", withEmployee.GetProperty("items")[0].Str("payrollName"));
        var advance = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("payrollId").ValueKind == JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, advance.GetProperty("payrollName").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.S.Owner.GetAsync($"{Payments}?payrollId=not-a-guid")).StatusCode);
    }

    [Fact]
    public async Task The_payments_list_filters_by_method_kind_and_status_and_sums_the_whole_filtered_set()
    {
        await using var factory = new AuthApiFactory();
        var f = await FinalizedSheetAsync(factory);
        var paid = await (await PayAsync(f.S.Owner, f.Sheet.GuidOf(), "Cash", (f.SlipAn, 640_000))).BodyAsync(HttpStatusCode.Created);
        await PayAsync(f.S.Owner, f.Sheet.GuidOf(), "BankTransfer", (f.SlipBinh, 320_000));
        await PayEmployeeAsync(f.S.Owner, f.An, 5_000);
        var cancel = await f.S.Owner.PostAsJsonAsync($"/api/cash-book/vouchers/{paid.GetProperty("vouchers")[0].GuidOf()}/cancel", new { reason = "Chi nhầm" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        async Task<JsonElement> ListAsync(string query) => await f.S.Owner.JsonAsync($"{Payments}?{query}");
        var all = await ListAsync("pageSize=1");
        var cash = await ListAsync("method=Cash");
        var bank = await ListAsync("method=BankTransfer");
        var advance = await ListAsync("kind=Advance");
        var wage = await ListAsync("kind=Wage&pageSize=1");
        var recorded = await ListAsync("status=Recorded");
        var cancelled = await ListAsync("status=Cancelled");
        var combined = await ListAsync("method=Cash&kind=Wage&status=Recorded");

        // Three payments, one cancelled: the sum counts the Recorded ones of the whole set, not only the one on the page.
        Assert.Equal((3, 1), (all.GetProperty("total").GetInt32(), all.GetProperty("items").GetArrayLength()));
        Assert.Equal(325_000m, all.GetProperty("summary").Money("amount"));
        Assert.Equal((2, 5_000m), (cash.GetProperty("total").GetInt32(), cash.GetProperty("summary").Money("amount")));
        Assert.Equal((1, 320_000m), (bank.GetProperty("total").GetInt32(), bank.GetProperty("summary").Money("amount")));
        Assert.Equal((1, 5_000m), (advance.GetProperty("total").GetInt32(), advance.GetProperty("summary").Money("amount")));
        Assert.Equal((2, 320_000m), (wage.GetProperty("total").GetInt32(), wage.GetProperty("summary").Money("amount")));
        Assert.Equal((2, 325_000m), (recorded.GetProperty("total").GetInt32(), recorded.GetProperty("summary").Money("amount")));
        Assert.Equal((1, 0m), (cancelled.GetProperty("total").GetInt32(), cancelled.GetProperty("summary").Money("amount")));
        Assert.Equal((0, 0m), (combined.GetProperty("total").GetInt32(), combined.GetProperty("summary").Money("amount")));
        foreach (var bad in new[] { "method=Card", "method=cash", "method=0", "kind=Salary", "kind=1", "status=Paid", "status=recorded" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await f.S.Owner.GetAsync($"{Payments}?{bad}")).StatusCode);
        }
    }
}
