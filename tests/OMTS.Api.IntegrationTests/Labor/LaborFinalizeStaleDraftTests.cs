using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-08 "refused if any payslip has missingRate" and "the month is locked": attendance saved after the draft was made
/// must not be locked unpaid by the finalization. PENDING: finalize trusts the flags and lines of the last refresh.
/// </summary>
public class LaborFinalizeStaleDraftTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Attendance_without_a_rate_saved_after_the_draft_still_blocks_finalize_with_MSG50()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000, r => r.ValidTo = month.Day(10));
        await w.AddAttendanceAsync(month.Day(2), null, worker.Id);
        await w.CreatePayrollAsync(month);
        await ExpectAsync(await w.Owner.PutAsJsonAsync($"{w.Attendance}/{month.Day(20):yyyy-MM-dd}",
            new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);

        var problem = await (await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG50");

        var entry = Assert.Single(problem.GetProperty("missing").EnumerateArray());
        Assert.Equal(worker.Id, entry.GetProperty("payee").GetProperty("id").GetGuid());
        Assert.Contains(month.Day(20).ToString("yyyy-MM-dd"), entry.GetProperty("dates").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task Paid_attendance_saved_after_the_draft_is_either_paid_by_the_finalized_payslip_or_finalize_is_refused()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        var newcomer = await w.AddWorkerAsync();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        await w.AddRateAsync(month, null, newcomer.Id, SalaryType.DailyWage, 300_000);
        await w.AddAttendanceAsync(month.Day(2), null, worker.Id);
        await w.CreatePayrollAsync(month);
        foreach (var person in new[] { worker.Id, newcomer.Id })
        {
            await ExpectAsync(await w.Owner.PutAsJsonAsync($"{w.Attendance}/{month.Day(3):yyyy-MM-dd}",
                new { rows = new[] { new { workerId = person, result = "Present" } } }), HttpStatusCode.OK);
        }

        var finalize = await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null);

        if (finalize.StatusCode == HttpStatusCode.OK)
        {
            var payroll = await ExpectAsync(finalize, HttpStatusCode.OK);
            Assert.Equal(600_000m, payroll.PayslipOf(worker.Id).GetProperty("net").GetDecimal());
            Assert.Equal(300_000m, payroll.PayslipOf(newcomer.Id).GetProperty("net").GetDecimal());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, finalize.StatusCode);
        }
    }
}
