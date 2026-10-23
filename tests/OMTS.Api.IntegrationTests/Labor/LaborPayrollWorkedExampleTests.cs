using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.LaborCost;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-06/07/09 worked examples through the real API only (rates, attendance sheets, assignments, completion, payroll,
/// payments): each salary type with allowances and deductions, a mid-month rate change, piecework in the month of its
/// completion date and a team with a crew leader.
/// </summary>
public class LaborPayrollWorkedExampleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static int _pairs;

    /// <summary>Two consecutive months used by no other test of this class (far before the months of <see cref="NewMonth"/>).</summary>
    private static (PayrollPeriod First, PayrollPeriod Next) ConsecutiveMonths()
    {
        var start = new DateOnly(2005, 1, 1).AddMonths(2 * (Interlocked.Increment(ref _pairs) - 1));
        var next = start.AddMonths(1);
        return (new PayrollPeriod(start.Year, start.Month), new PayrollPeriod(next.Year, next.Month));
    }

    private Task<(HttpClient Client, OMTS.Domain.Entities.Identity.Account Account)> AccountantAsync() =>
        factory.StaffAsync(null, (PermissionFunctions.PayRules, true, true, true, false), (PermissionFunctions.Payroll, true, true, true, false));

    private static string SheetOf(LaborWorld w, DateOnly date) => $"{w.Attendance}/{date:yyyy-MM-dd}";

    private static List<JsonElement> Lines(JsonElement payslip, string type) =>
        payslip.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("type").GetString() == type).ToList();

    [Fact]
    public async Task Per_working_day_salary_with_per_paid_day_and_monthly_components_and_an_advance_is_a_worked_example()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var (accountant, _) = await AccountantAsync();
        var (manager, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.Attendance, true, true, true, false));
        var employee = await w.AddEmployeeAsync();
        await ExpectAsync(await accountant.PostAsJsonAsync("/api/pay-rates", new
        {
            employeeId = employee.Id, salaryType = "PerWorkingDay", amount = 8_000_000, standardWorkingDays = 26,
            components = new object[]
            {
                new { kind = "Allowance", name = "Ăn trưa", amount = 30_000, basis = "PerPaidDay" },
                new { kind = "Allowance", name = "Điện thoại", amount = 200_000, basis = "PerMonth" },
                new { kind = "Deduction", name = "BHXH", amount = 150_000, basis = "PerMonth" },
            },
            validFrom = month.Start, validTo = month.End,
        }), HttpStatusCode.Created);
        // Hours are informational: 12 hours on a day is no overtime.
        foreach (var (day, result, hours) in new[] { (2, "Present", 12m), (3, "Present", 8m), (4, "Present", 4m), (5, "PaidLeave", 8m), (6, "UnpaidLeave", 8m) })
        {
            await ExpectAsync(await manager.PutAsJsonAsync(SheetOf(w, month.Day(day)), new { rows = new[] { new { employeeId = employee.Id, result, hours } } }), HttpStatusCode.OK);
        }

        await ExpectAsync(await accountant.PostAsJsonAsync("/api/salary-advances",
            new { employeeId = employee.Id, amount = 400_000, paymentDate = month.Day(10), method = "Cash" }), HttpStatusCode.Created);

        var slip = (await w.CreatePayrollAsync(month, accountant)).PayslipOf(employee.Id);

        // 4 paid days (3 Present + 1 PaidLeave): 8,000,000 × 4 ÷ 26 = 1,230,769.23 → 1,231,000.
        var salary = Assert.Single(Lines(slip, "MonthlySalary"));
        Assert.Equal((4m, 8_000_000m, 1_231_000m), (salary.GetProperty("quantity").GetDecimal(), salary.GetProperty("unitRate").GetDecimal(), salary.GetProperty("amount").GetDecimal()));
        Assert.Equal([120_000m, 200_000m], Lines(slip, "Allowance").Select(l => l.GetProperty("amount").GetDecimal()).Order());
        Assert.Equal(150_000m, Assert.Single(Lines(slip, "Deduction")).GetProperty("amount").GetDecimal());
        Assert.Equal(400_000m, Assert.Single(Lines(slip, "AdvanceDeduction")).GetProperty("amount").GetDecimal());
        Assert.Equal((1_551_000m, 150_000m, 400_000m, 1_001_000m),
            (slip.GetProperty("gross").GetDecimal(), slip.GetProperty("deductions").GetDecimal(), slip.GetProperty("advanceDeduction").GetDecimal(), slip.GetProperty("net").GetDecimal()));
        Assert.False(slip.GetProperty("missingRate").GetBoolean());
    }

    [Fact]
    public async Task Monthly_fixed_salary_is_never_prorated_while_per_paid_day_allowances_follow_the_paid_days()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var (accountant, _) = await AccountantAsync();
        var employee = await w.AddEmployeeAsync();
        await ExpectAsync(await accountant.PostAsJsonAsync("/api/pay-rates", new
        {
            employeeId = employee.Id, salaryType = "MonthlyFixed", amount = 6_000_000,
            components = new[] { new { kind = "Allowance", name = "Xăng xe", amount = 25_000, basis = "PerPaidDay" } },
            validFrom = month.Start, validTo = month.End,
        }), HttpStatusCode.Created);
        foreach (var (day, result) in new[] { (2, "Present"), (3, "Present"), (4, "UnpaidLeave"), (5, "UnpaidLeave") })
        {
            await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(day)), new { rows = new[] { new { employeeId = employee.Id, result } } }), HttpStatusCode.OK);
        }

        var slip = (await w.CreatePayrollAsync(month, accountant)).PayslipOf(employee.Id);

        Assert.Equal(6_000_000m, Assert.Single(Lines(slip, "MonthlySalary")).GetProperty("amount").GetDecimal());
        var allowance = Assert.Single(Lines(slip, "Allowance"));
        Assert.Equal((2m, 50_000m), (allowance.GetProperty("quantity").GetDecimal(), allowance.GetProperty("amount").GetDecimal()));
        Assert.Equal(6_050_000m, slip.GetProperty("net").GetDecimal());
    }

    [Fact]
    public async Task Mid_month_daily_rate_change_saved_through_the_api_pays_each_paid_day_at_its_own_rate()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var (accountant, _) = await AccountantAsync();
        var worker = await w.AddWorkerAsync();
        var first = await ExpectAsync(await accountant.PostAsJsonAsync("/api/pay-rates",
            new { workerId = worker.Id, salaryType = "DailyWage", amount = 300_000, validFrom = month.Start }), HttpStatusCode.Created);
        var second = await ExpectAsync(await accountant.PostAsJsonAsync("/api/pay-rates",
            new { workerId = worker.Id, salaryType = "DailyWage", amount = 350_000, validFrom = month.Day(16), validTo = month.End }), HttpStatusCode.Created);
        foreach (var (day, result) in new[] { (10, "Present"), (20, "Present"), (21, "PaidLeave"), (22, "UnpaidLeave") })
        {
            await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(day)), new { rows = new[] { new { workerId = worker.Id, result } } }), HttpStatusCode.OK);
        }

        var slip = (await w.CreatePayrollAsync(month, accountant)).PayslipOf(worker.Id);

        var firstId = first.GetProperty("rate").GetProperty("id").GetGuid();
        Assert.Equal((firstId, month.Day(15).ToString("yyyy-MM-dd")),
            (second.GetProperty("closedPrevious").GetProperty("id").GetGuid(), second.GetProperty("closedPrevious").GetProperty("validTo").GetString()));
        Assert.Equal([300_000m, 350_000m, 350_000m],
            Lines(slip, "DailyWage").OrderBy(l => l.GetProperty("payDate").GetString()).Select(l => l.GetProperty("amount").GetDecimal()));
        Assert.Equal(1_000_000m, slip.GetProperty("net").GetDecimal());
        Assert.Contains(await factory.AuditOfAsync(firstId), e => e.Action == "PayRuleVersion.Closed");
    }

    [Fact]
    public async Task Piecework_is_paid_in_the_month_of_its_completion_date_and_unconfirmed_piecework_never()
    {
        var w = await factory.WorldAsync();
        var (workMonth, completionMonth) = ConsecutiveMonths();
        var worker = await w.AddWorkerAsync();
        object Piece(DateOnly date) => new { workDate = date, workerId = worker.Id, task = "Vệ sinh lồng", paymentType = "Piecework", pieceUnit = "cage", plannedQuantity = 10, pieceRate = 15_500 };
        var piece = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Piece(workMonth.Day(28))), HttpStatusCode.Created);
        var confirmed = await ExpectAsync(await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.GetProperty("id").GetGuid()}/completion",
            new { completedQuantity = 7.5m, completionDate = completionMonth.Day(2) }), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Piece(completionMonth.Day(3))), HttpStatusCode.Created);

        var workPayroll = await w.CreatePayrollAsync(workMonth);
        var completionPayroll = await w.CreatePayrollAsync(completionMonth);

        // 7.5 × 15,500 = 116,250 → 117,000.
        Assert.Equal(117_000m, confirmed.GetProperty("completion").GetProperty("pieceworkAmount").GetDecimal());
        Assert.DoesNotContain(workPayroll.GetProperty("payslips").EnumerateArray(), p => p.GetProperty("payee").GetProperty("id").GetGuid() == worker.Id);
        var line = Assert.Single(completionPayroll.PayslipOf(worker.Id).GetProperty("lines").EnumerateArray());
        Assert.Equal(("Piecework", 7.5m, 15_500m, 117_000m),
            (line.GetProperty("type").GetString(), line.GetProperty("quantity").GetDecimal(), line.GetProperty("unitRate").GetDecimal(), line.GetProperty("amount").GetDecimal()));
    }

    [Fact]
    public async Task An_advance_deducted_by_two_drafts_is_never_deducted_twice_finalize_recomputes_the_later_month()
    {
        var w = await factory.WorldAsync();
        var (first, second) = ConsecutiveMonths();
        var worker = await w.AddWorkerAsync();
        foreach (var month in new[] { first, second })
        {
            await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
            await w.AddAttendanceAsync(month.Day(2), null, worker.Id);
        }

        var advance = await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances",
            new { workerId = worker.Id, amount = 500_000, paymentDate = first.Day(5), method = "Cash" }), HttpStatusCode.Created);
        var firstDraft = (await w.CreatePayrollAsync(first)).PayslipOf(worker.Id);
        var secondDraft = (await w.CreatePayrollAsync(second)).PayslipOf(worker.Id);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{first.Key}/finalize", null), HttpStatusCode.OK);

        // The second draft still deducts 300,000; finalize recomputes it against the 200,000 left.
        var refreshed = (await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{second.Key}/finalize", null), HttpStatusCode.OK)).PayslipOf(worker.Id);
        var after = (await ExpectAsync(await w.Owner.GetAsync($"/api/salary-advances?workerId={worker.Id}"), HttpStatusCode.OK))
            .EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == advance.GetProperty("id").GetGuid());

        // Both drafts saw 500,000 outstanding and capped it at 300,000; only 200,000 is left once the first month is final.
        Assert.Equal((300_000m, 300_000m), (firstDraft.GetProperty("advanceDeduction").GetDecimal(), secondDraft.GetProperty("advanceDeduction").GetDecimal()));
        Assert.Equal((200_000m, 100_000m), (refreshed.GetProperty("advanceDeduction").GetDecimal(), refreshed.GetProperty("net").GetDecimal()));
        Assert.Equal((0m, refreshed.GetProperty("id").GetGuid(), second.Key),
            (after.GetProperty("remaining").GetDecimal(), after.GetProperty("deductedInPayslipId").GetGuid(), after.GetProperty("deductedInPeriod").GetString()));
    }

    [Fact]
    public async Task An_advance_reversed_after_the_draft_and_a_rate_edit_are_picked_up_by_finalize()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var (accountant, _) = await AccountantAsync();
        var worker = await w.AddWorkerAsync();
        var rate = await ExpectAsync(await accountant.PostAsJsonAsync("/api/pay-rates",
            new { workerId = worker.Id, salaryType = "DailyWage", amount = 300_000, validFrom = month.Start, validTo = month.End }), HttpStatusCode.Created);
        await w.AddAttendanceAsync(month.Day(2), null, worker.Id);
        var advance = await ExpectAsync(await accountant.PostAsJsonAsync("/api/salary-advances",
            new { workerId = worker.Id, amount = 100_000, paymentDate = month.Day(1), method = "Cash" }), HttpStatusCode.Created);
        var draft = (await w.CreatePayrollAsync(month, accountant)).PayslipOf(worker.Id);
        await ExpectAsync(await accountant.PostAsJsonAsync($"/api/labor-payments/{advance.GetProperty("id").GetGuid()}/reverse", new { reason = "Ghi nhầm" }), HttpStatusCode.Created);
        await ExpectAsync(await accountant.PutAsJsonAsync($"/api/pay-rates/{rate.GetProperty("rate").GetProperty("id").GetGuid()}", new
        {
            salaryType = "DailyWage", amount = 320_000, validFrom = month.Start, validTo = month.End, reason = "Tăng giá công",
            rowVersion = rate.GetProperty("rate").GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);

        // Finalize recomputes the draft: the reversed advance is no longer deducted and the edited rate applies.
        var refreshed = (await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK)).PayslipOf(worker.Id);

        Assert.Equal((100_000m, 200_000m), (draft.GetProperty("advanceDeduction").GetDecimal(), draft.GetProperty("net").GetDecimal()));
        Assert.Equal((0m, 320_000m), (refreshed.GetProperty("advanceDeduction").GetDecimal(), refreshed.GetProperty("net").GetDecimal()));
    }

    [Fact]
    public async Task Team_daily_wage_goes_to_each_in_use_member_and_team_piecework_to_one_team_payslip_paid_to_the_crew_leader()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var (accountant, _) = await AccountantAsync();
        var (manager, _) = await factory.StaffAsync(w.Zone,
            (PermissionFunctions.LaborWorkers, true, true, true, false), (PermissionFunctions.WorkAssignments, true, true, true, true),
            (PermissionFunctions.Attendance, true, true, true, false));
        var team = await ExpectAsync(await manager.PostAsJsonAsync("/api/labor/teams", new { name = $"Tổ Nam {NewCode()}" }), HttpStatusCode.Created);
        var teamId = team.GetProperty("id").GetGuid();
        var members = new List<JsonElement>();
        foreach (var name in new[] { "Lê Văn Một", "Lê Văn Hai", "Lê Văn Ba" })
        {
            members.Add(await ExpectAsync(await manager.PostAsJsonAsync("/api/labor/workers", new { fullName = $"{name} {NewCode()}", phoneNumber = NewPhone(), teamId }), HttpStatusCode.Created));
        }

        var (leader, second, gone) = (members[0].GetProperty("id").GetGuid(), members[1].GetProperty("id").GetGuid(), members[2].GetProperty("id").GetGuid());
        await ExpectAsync(await manager.PostAsync($"/api/labor/workers/{gone}/deactivate", null), HttpStatusCode.OK);
        var current = await ExpectAsync(await manager.GetAsync($"/api/labor/teams/{teamId}"), HttpStatusCode.OK);
        await ExpectAsync(await manager.PutAsJsonAsync($"/api/labor/teams/{teamId}", new
        {
            name = team.GetProperty("name").GetString(), leaderWorkerId = leader, rowVersion = current.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);
        await ExpectAsync(await manager.PostAsJsonAsync(w.Assignments, new { workDate = month.Day(3), teamId, task = "Kéo bè", paymentType = "DailyWage" }), HttpStatusCode.Created);
        var suggested = await ExpectAsync(await manager.GetAsync($"{w.Attendance}?date={month.Day(3):yyyy-MM-dd}"), HttpStatusCode.OK);
        await ExpectAsync(await manager.PutAsJsonAsync(SheetOf(w, month.Day(3)), new { rows = new[] { new { teamId, result = "Present", hours = 8 } } }), HttpStatusCode.OK);
        var piece = await ExpectAsync(await manager.PostAsJsonAsync(w.Assignments, new
        {
            workDate = month.Day(4), teamId, task = "Vệ sinh lồng", paymentType = "Piecework", pieceUnit = "cage", plannedQuantity = 10, pieceRate = 15_500,
        }), HttpStatusCode.Created);
        await ExpectAsync(await manager.PostAsJsonAsync($"{w.Assignments}/{piece.GetProperty("id").GetGuid()}/completion",
            new { completedQuantity = 7.5m, completionDate = month.Day(5) }), HttpStatusCode.OK);
        await ExpectAsync(await accountant.PostAsJsonAsync("/api/pay-rates/bulk", new
        {
            people = new[] { new { workerId = leader }, new { workerId = second } },
            rate = new { salaryType = "DailyWage", amount = 250_000, validFrom = month.Start, validTo = month.End },
        }), HttpStatusCode.Created);

        var payroll = await w.CreatePayrollAsync(month, accountant);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var teamSlip = payroll.PayslipOf(teamId);
        var teamPayment = await ExpectAsync(await accountant.PostAsJsonAsync($"/api/payslips/{teamSlip.GetProperty("id").GetGuid()}/payments",
            new { amountPaid = 117_000, paymentDate = month.End, method = "Cash" }), HttpStatusCode.Created);
        await ExpectAsync(await accountant.PostAsJsonAsync("/api/payslips/payments/bulk", new
        {
            payslipIds = new[] { payroll.PayslipOf(leader).GetProperty("id").GetGuid(), payroll.PayslipOf(second).GetProperty("id").GetGuid() },
            paymentDate = month.End, method = "BankTransfer",
        }), HttpStatusCode.Created);
        var months = await ExpectAsync(await accountant.GetAsync($"/api/payrolls?year={month.Year}"), HttpStatusCode.OK);

        Assert.Equal(new[] { leader, second }.Order(),
            suggested.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("suggested").GetBoolean()).Select(r => r.GetProperty("personId").GetGuid()).Order());
        Assert.Equal((250_000m, 250_000m), (payroll.PayslipOf(leader).GetProperty("net").GetDecimal(), payroll.PayslipOf(second).GetProperty("net").GetDecimal()));
        Assert.Equal(("Team", 117_000m), (teamSlip.GetProperty("payee").GetProperty("kind").GetString(), teamSlip.GetProperty("net").GetDecimal()));
        Assert.DoesNotContain(payroll.GetProperty("payslips").EnumerateArray(), p => p.GetProperty("payee").GetProperty("id").GetGuid() == gone);
        Assert.Equal(members[0].GetProperty("fullName").GetString(), teamPayment.GetProperty("payment").GetProperty("recipient").GetString());
        Assert.Equal("Fully paid", months.EnumerateArray().Single(p => p.GetProperty("period").GetString() == month.Key).GetProperty("state").GetString());
    }
}
