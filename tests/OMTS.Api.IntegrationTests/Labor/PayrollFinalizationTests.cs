using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-08 finalize, send back and reopen: Owner only, missing basis, repeated finalization, payments recorded, reasons, period lock.</summary>
public class PayrollFinalizationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Finalize_approves_every_payslip_locks_the_month_and_clears_the_return_reason()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var employee = await w.AddEmployeeAsync();
        await w.AddRateAsync(month, employee.Id, null, SalaryType.MonthlyFixed, 6_000_000);
        await w.CreatePayrollAsync(month);
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/send-back", new { reason = "Thiếu công ngày 5" }), HttpStatusCode.OK);

        var finalized = await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var locked = await w.Owner.PostAsJsonAsync(w.Assignments, new { workDate = month.Day(3), employeeId = employee.Id, task = "x", paymentType = "DailyWage" });

        var slip = finalized.PayslipOf(employee.Id);
        Assert.Equal(("Finalized", "Approved"), (finalized.GetProperty("state").GetString(), slip.GetProperty("status").GetString()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, finalized.GetProperty("returnReason").ValueKind);
        await locked.ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        Assert.Contains(await factory.AuditOfAsync(slip.GetProperty("id").GetGuid()), e => e.Action == "LaborSettlement.StatusChanged");
    }

    [Fact]
    public async Task The_recompute_done_by_finalize_is_audited_as_Payroll_Refreshed_even_when_finalize_is_refused()
    {
        var w = await factory.WorldAsync();
        var (refused, done) = (NewMonth(), NewMonth());
        var worker = await w.AddWorkerAsync();
        var employee = await w.AddEmployeeAsync();
        await w.AddAttendanceAsync(refused.Day(4), null, worker.Id);
        await w.AddRateAsync(done, employee.Id, null, SalaryType.MonthlyFixed, 6_000_000);
        await w.CreatePayrollAsync(refused);
        await w.CreatePayrollAsync(done);
        Task<List<string>> ActionsAsync(PayrollPeriod month) => factory.QueryAsync(db =>
            db.AuditEvents.AsNoTracking().Where(e => e.RecordCode == month.Key).OrderBy(e => e.OccurredAt).Select(e => e.Action).ToListAsync());

        await (await w.Owner.PostAsync($"/api/payrolls/{refused.Key}/finalize", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG50");
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{done.Key}/finalize", null), HttpStatusCode.OK);

        Assert.Equal(1, (await ActionsAsync(refused)).Count(a => a == "Payroll.Refreshed"));
        var actions = await ActionsAsync(done);
        Assert.Equal(1, actions.Count(a => a == "Payroll.Refreshed"));
        Assert.True(actions.IndexOf("Payroll.Refreshed") < actions.IndexOf("Payroll.Finalized"));
    }

    [Fact]
    public async Task Only_the_owner_finalizes_sends_back_or_reopens_MSG02()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var employee = await w.AddEmployeeAsync();
        await w.AddRateAsync(month, employee.Id, null, SalaryType.MonthlyFixed, 6_000_000);
        var (accountant, _) = await factory.StaffAsync(null, (PermissionFunctions.Payroll, true, true, true, false));
        await w.CreatePayrollAsync(month, accountant);

        await (await accountant.PostAsync($"/api/payrolls/{month.Key}/finalize", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await accountant.PostAsJsonAsync($"/api/payrolls/{month.Key}/send-back", new { reason = "x" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await accountant.PostAsJsonAsync($"/api/payrolls/{month.Key}/reopen", new { reason = "x" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
    }

    [Fact]
    public async Task Missing_rates_or_an_empty_payroll_are_MSG50_with_the_payees_and_dates()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var empty = NewMonth();
        var worker = await w.AddWorkerAsync(configure: x => x.FullName = $"Hồ Văn {Guid.NewGuid():N}"[..20]);
        await w.AddAttendanceAsync(month.Day(4), null, worker.Id);
        await w.CreatePayrollAsync(month);
        await w.CreatePayrollAsync(empty);

        var missing = await (await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG50");
        var nothing = await (await w.Owner.PostAsync($"/api/payrolls/{empty.Key}/finalize", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG50");

        Assert.Equal("Attendance or the effective salary/rate is incomplete, so it cannot be finalized yet.", missing.GetProperty("detail").GetString());
        var entry = Assert.Single(missing.GetProperty("missing").EnumerateArray());
        Assert.Equal(worker.Id, entry.GetProperty("payee").GetProperty("id").GetGuid());
        Assert.Equal([month.Day(4).ToString("yyyy-MM-dd")], entry.GetProperty("dates").EnumerateArray().Select(d => d.GetString()));
        Assert.True(nothing.GetProperty("empty").GetBoolean());
    }

    [Fact]
    public async Task Finalizing_a_finalized_month_is_MSG61()
    {
        var w = await factory.WorldAsync();
        var month = await w.FinalizedMonthAsync();

        var problem = await (await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG61");

        Assert.Equal($"Cannot change Payroll {month.Label} from “Approved” to “Approved”: the payroll is already finalized.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Reopen_with_payments_recorded_is_MSG61_without_payments_it_unlocks_the_month()
    {
        var w = await factory.WorldAsync();
        var paidMonth = await w.FinalizedMonthAsync();
        var paidSlip = (await ExpectAsync(await w.Owner.GetAsync($"/api/payrolls/{paidMonth.Key}"), HttpStatusCode.OK)).GetProperty("payslips")[0].GetProperty("id").GetGuid();
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payslips/{paidSlip}/payments",
            new { amountPaid = 1_000_000, paymentDate = paidMonth.End, method = "Cash" }), HttpStatusCode.Created);
        var openMonth = await w.FinalizedMonthAsync();
        var worker = await w.AddWorkerAsync();

        var refused = await (await w.Owner.PostAsJsonAsync($"/api/payrolls/{paidMonth.Key}/reopen", new { reason = "Sai" })).ProblemAsync(HttpStatusCode.Conflict, "MSG61");
        var reopened = await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{openMonth.Key}/reopen", new { reason = "Sai công" }), HttpStatusCode.OK);
        var unlocked = await w.Owner.PostAsJsonAsync(w.Assignments, new { workDate = openMonth.Day(2), workerId = worker.Id, task = "x", paymentType = "DailyWage" });

        Assert.Equal($"Cannot change Payroll {paidMonth.Label} from “Approved” to “Draft”: payments have already been recorded.", refused.GetProperty("detail").GetString());
        Assert.Equal(("Draft", "Draft"), (reopened.GetProperty("state").GetString(), reopened.GetProperty("payslips")[0].GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.Created, unlocked.StatusCode);
        var slipId = reopened.GetProperty("payslips")[0].GetProperty("id").GetGuid();
        Assert.Contains(await factory.AuditOfAsync(slipId), e => e.Action == "LaborSettlement.StatusChanged" && e.Reason == "Sai công");
    }

    [Fact]
    public async Task Send_back_or_reopen_without_a_reason_is_MSG01()
    {
        var w = await factory.WorldAsync();
        var draft = NewMonth();
        await w.CreatePayrollAsync(draft);
        var finalized = await w.FinalizedMonthAsync();

        var sendBack = await (await w.Owner.PostAsJsonAsync($"/api/payrolls/{draft.Key}/send-back", new { reason = "" })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var reopen = await (await w.Owner.PostAsJsonAsync($"/api/payrolls/{finalized.Key}/reopen", new { })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal(("Reason", "Reason"), (sendBack.Field(), reopen.Field()));
    }

    [Fact]
    public async Task Send_back_keeps_the_draft_stores_the_reason_and_notifies_the_preparer()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var employee = await w.AddEmployeeAsync();
        await w.AddRateAsync(month, employee.Id, null, SalaryType.MonthlyFixed, 6_000_000);
        var (accountant, accountantAccount) = await factory.StaffAsync(null, (PermissionFunctions.Payroll, true, true, true, false));
        await w.CreatePayrollAsync(month, accountant);

        var sentBack = await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/send-back", new { reason = "Kiểm tra lại phụ cấp" }), HttpStatusCode.OK);

        Assert.Equal(("Sent back", "Kiểm tra lại phụ cấp"), (sentBack.GetProperty("state").GetString(), sentBack.GetProperty("returnReason").GetString()));
        Assert.Equal("Draft", sentBack.PayslipOf(employee.Id).GetProperty("status").GetString());
        var notice = await factory.QueryAsync(db => db.Notifications.AsNoTracking()
            .SingleAsync(n => n.RecipientAccountId == accountantAccount.Id && n.Type == NotificationTypes.PayrollSentBack));
        Assert.Equal($"Payroll {month.Label} was sent back: Kiểm tra lại phụ cấp", notice.Body);
    }

    [Fact]
    public async Task Finalized_payslips_keep_their_amounts_when_rates_change_later()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        var rate = await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        await w.AddAttendanceAsync(month.Day(2), null, worker.Id);
        await w.CreatePayrollAsync(month);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);

        await factory.UpdateAsync<PayRuleVersion>(rate.Id, v => v.Amount = 400_000);
        var payroll = await ExpectAsync(await w.Owner.GetAsync($"/api/payrolls/{month.Key}"), HttpStatusCode.OK);

        Assert.Equal(300_000m, payroll.PayslipOf(worker.Id).GetProperty("net").GetDecimal());
        Assert.Equal(StatusIds.LaborSettlement.Approved, payroll.PayslipOf(worker.Id).GetProperty("statusId").GetInt32());
    }
}
