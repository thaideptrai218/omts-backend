using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-07 payroll: calculation from attendance, piecework and rates, duplicates, manual lines, refresh, missing rates, advances.</summary>
public class PayrollPreparationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Payroll_is_filled_from_rates_attendance_and_confirmed_piecework()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var employee = await w.AddEmployeeAsync();
        var worker = await w.AddWorkerAsync();
        var team = await w.AddTeamAsync();
        await w.AddRateAsync(month, employee.Id, null, SalaryType.MonthlyFixed, 8_000_000);
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        for (var day = 1; day <= 3; day++)
        {
            await w.AddAttendanceAsync(month.Day(day), null, worker.Id);
        }

        await w.AddAttendanceAsync(month.Day(4), null, worker.Id, AttendanceResult.UnpaidLeave);
        await w.AddPieceworkAsync(month.Day(20), null, null, team.Id, completed: 37, rate: 2_500);
        await w.AddPieceworkAsync(month.Day(21), null, null, team.Id);
        var (accountant, accountantAccount) = await factory.StaffAsync(null, (PermissionFunctions.Payroll, true, true, true, false));

        var payroll = await w.CreatePayrollAsync(month, accountant);

        Assert.Equal(("Draft", month.Key), (payroll.GetProperty("state").GetString(), payroll.GetProperty("period").GetString()));
        Assert.Equal(8_000_000m, payroll.PayslipOf(employee.Id).GetProperty("net").GetDecimal());
        var workerSlip = payroll.PayslipOf(worker.Id);
        Assert.Equal((900_000m, 3), (workerSlip.GetProperty("net").GetDecimal(), workerSlip.GetProperty("lines").GetArrayLength()));
        var teamSlip = payroll.PayslipOf(team.Id);
        Assert.Equal(("Team", 93_000m), (teamSlip.GetProperty("payee").GetProperty("kind").GetString(), teamSlip.GetProperty("net").GetDecimal()));
        Assert.Equal("Draft", workerSlip.GetProperty("status").GetString());
        var list = await ExpectAsync(await accountant.GetAsync($"/api/payrolls?year={month.Year}"), HttpStatusCode.OK);
        Assert.Contains(list.EnumerateArray(), p => p.GetProperty("period").GetString() == month.Key && p.GetProperty("state").GetString() == "Draft");
        var created = Assert.Single(await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Where(e => e.Action == "Payroll.Created" && e.RecordCode == month.Key).ToList())));
        Assert.Equal(accountantAccount.Id, created.ActorAccountId);
    }

    [Fact]
    public async Task Month_that_already_has_a_payroll_is_MSG03()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        await w.CreatePayrollAsync(month);

        var problem = await (await w.Owner.PostAsJsonAsync("/api/payrolls", new { period = month.Key })).ProblemAsync(HttpStatusCode.Conflict, "MSG03");
        var invalid = await w.Owner.PostAsJsonAsync("/api/payrolls", new { period = "10/2026" });

        Assert.Equal($"Payroll {month.Label} already exists. The system opens the existing record for you to check and does not create a copy.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(month.Key, problem.GetProperty("period").GetString());
        await invalid.ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
    }

    [Fact]
    public async Task Manual_lines_need_a_reason_thousands_and_keep_net_pay_not_negative()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        await w.AddAttendanceAsync(month.Day(2), null, worker.Id);
        var payslip = (await w.CreatePayrollAsync(month)).PayslipOf(worker.Id).GetProperty("id").GetGuid();
        var lines = $"/api/payrolls/{month.Key}/payslips/{payslip}/lines";

        var noReason = await (await w.Owner.PostAsJsonAsync(lines, new { type = "Addition", amount = 100_000 })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var odd = await (await w.Owner.PostAsJsonAsync(lines, new { type = "Addition", amount = 100_500, reason = "Thưởng" })).ProblemAsync(HttpStatusCode.BadRequest, "MSG41");
        var tooMuch = await (await w.Owner.PostAsJsonAsync(lines, new { type = "Deduction", amount = 400_000, reason = "Làm hỏng dây" })).ProblemAsync(HttpStatusCode.Conflict, "MSG45");
        var added = await ExpectAsync(await w.Owner.PostAsJsonAsync(lines, new { type = "Addition", amount = 200_000, reason = "Thưởng chuyên cần" }), HttpStatusCode.Created);

        Assert.Equal("Reason", noReason.Field());
        Assert.Equal("MSG41", odd.GetProperty("code").GetString());
        Assert.Equal("Deduction amount exceeds the allowed limit (300,000).", tooMuch.GetProperty("detail").GetString());
        Assert.Equal(500_000m, added.GetProperty("net").GetDecimal());
        Assert.Contains(added.GetProperty("lines").EnumerateArray(), l => l.GetProperty("isManual").GetBoolean() && l.GetProperty("reason").GetString() == "Thưởng chuyên cần");
    }

    [Fact]
    public async Task Refresh_recalculates_system_lines_and_keeps_manual_ones()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        await w.AddAttendanceAsync(month.Day(2), null, worker.Id);
        var payslip = (await w.CreatePayrollAsync(month)).PayslipOf(worker.Id).GetProperty("id").GetGuid();
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/payslips/{payslip}/lines",
            new { type = "Addition", amount = 50_000, reason = "Hỗ trợ xăng" }), HttpStatusCode.Created);
        await w.AddAttendanceAsync(month.Day(3), null, worker.Id);

        var refreshed = await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/refresh", null), HttpStatusCode.OK);

        var slip = refreshed.PayslipOf(worker.Id);
        Assert.Equal(650_000m, slip.GetProperty("net").GetDecimal());
        Assert.Equal(1, slip.GetProperty("lines").EnumerateArray().Count(l => l.GetProperty("isManual").GetBoolean()));
    }

    [Fact]
    public async Task Attendance_without_a_valid_rate_sets_missing_rate_and_the_draft_still_saves()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        await w.AddAttendanceAsync(month.Day(6), null, worker.Id);
        await w.AddAttendanceAsync(month.Day(7), null, worker.Id, AttendanceResult.PaidLeave);

        var slip = (await w.CreatePayrollAsync(month)).PayslipOf(worker.Id);

        Assert.True(slip.GetProperty("missingRate").GetBoolean());
        Assert.Equal([month.Day(6).ToString("yyyy-MM-dd"), month.Day(7).ToString("yyyy-MM-dd")],
            slip.GetProperty("missingRateDates").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task Refresh_and_manual_lines_of_a_finalized_month_are_locked()
    {
        var w = await factory.WorldAsync();
        var month = await w.FinalizedMonthAsync();
        var payroll = await ExpectAsync(await w.Owner.GetAsync($"/api/payrolls/{month.Key}"), HttpStatusCode.OK);
        var payslip = payroll.GetProperty("payslips")[0].GetProperty("id").GetGuid();

        await (await w.Owner.PostAsync($"/api/payrolls/{month.Key}/refresh", null)).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        await (await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/payslips/{payslip}/lines", new { type = "Addition", amount = 1_000, reason = "x" }))
            .ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
    }

    [Fact]
    public async Task Outstanding_advances_become_one_capped_deduction_and_the_rest_stays_outstanding()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        for (var day = 1; day <= 3; day++)
        {
            await w.AddAttendanceAsync(month.Day(day), null, worker.Id);
        }

        var advance = await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances", new
        {
            workerId = worker.Id, amount = 1_000_000, paymentDate = month.Day(5), method = "Cash",
        }), HttpStatusCode.Created);
        var lateAdvance = await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances", new
        {
            workerId = worker.Id, amount = 200_000, paymentDate = month.End.AddDays(1), method = "Cash",
        }), HttpStatusCode.Created);

        var slip = (await w.CreatePayrollAsync(month)).PayslipOf(worker.Id);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var outstanding = await ExpectAsync(await w.Owner.GetAsync($"/api/salary-advances?outstanding=true&workerId={worker.Id}"), HttpStatusCode.OK);

        Assert.Equal((900_000m, 0m), (slip.GetProperty("advanceDeduction").GetDecimal(), slip.GetProperty("net").GetDecimal()));
        var rows = outstanding.EnumerateArray().ToDictionary(a => a.GetProperty("id").GetGuid(), a => a.GetProperty("remaining").GetDecimal());
        Assert.Equal(100_000m, rows[advance.GetProperty("id").GetGuid()]);
        Assert.Equal(200_000m, rows[lateAdvance.GetProperty("id").GetGuid()]);
    }

    [Fact]
    public async Task Fully_deducted_advances_point_to_the_payslip_and_reopening_releases_them()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        await w.AddAttendanceAsync(month.Day(1), null, worker.Id);
        var advance = await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances", new
        {
            workerId = worker.Id, amount = 100_000, paymentDate = month.Day(1), method = "Cash",
        }), HttpStatusCode.Created);
        var payslip = (await w.CreatePayrollAsync(month)).PayslipOf(worker.Id).GetProperty("id").GetGuid();

        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var deducted = await AdvanceAsync(w, worker.Id, advance.GetProperty("id").GetGuid());
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/reopen", new { reason = "Sai công" }), HttpStatusCode.OK);
        var released = await AdvanceAsync(w, worker.Id, advance.GetProperty("id").GetGuid());

        Assert.Equal((payslip, month.Key, 0m), (deducted.GetProperty("deductedInPayslipId").GetGuid(), deducted.GetProperty("deductedInPeriod").GetString(), deducted.GetProperty("remaining").GetDecimal()));
        Assert.Equal((JsonValueKind.Null, 100_000m), (released.GetProperty("deductedInPayslipId").ValueKind, released.GetProperty("remaining").GetDecimal()));
    }

    private static async Task<JsonElement> AdvanceAsync(LaborWorld w, Guid workerId, Guid advanceId) =>
        (await ExpectAsync(await w.Owner.GetAsync($"/api/salary-advances?workerId={workerId}"), HttpStatusCode.OK))
            .EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == advanceId);
}
