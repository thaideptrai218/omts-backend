using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-10 "own data only" (review): a Zone Manager may edit the worker→employee link of any seasonal worker, so that
/// link must never open wage data. Linking a colleague's worker to oneself shows nothing in /api/me/pay.
/// </summary>
public class LaborPersonalPayOwnDataTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Zone_manager_who_links_a_colleague_s_worker_to_himself_still_cannot_read_the_wages()
    {
        var w = await factory.WorldAsync();
        var (manager, managerAccount) = await factory.StaffAsync(w.Zone, (PermissionFunctions.LaborWorkers, true, true, true, false));
        var managerEmployee = await w.AddEmployeeAsync(configure: e => e.AccountId = managerAccount.Id);
        var victim = await w.AddWorkerAsync();
        var month = NewMonth();
        await w.AddRateAsync(month, null, victim.Id, SalaryType.DailyWage, 400_000);
        await w.AddAttendanceAsync(month.Day(9), null, victim.Id);
        var payroll = await w.CreatePayrollAsync(month);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var payslipId = payroll.PayslipOf(victim.Id).GetProperty("id").GetGuid();

        var current = await ExpectAsync(await manager.GetAsync($"/api/labor/workers/{victim.Id}"), HttpStatusCode.OK);
        var link = await manager.PutAsJsonAsync($"/api/labor/workers/{victim.Id}", new
        {
            fullName = current.GetProperty("fullName").GetString(), employeeId = managerEmployee.Id, rowVersion = current.GetProperty("rowVersion").GetString(),
        });

        // Whether or not the link itself is accepted, the wages stay out of reach.
        Assert.True(link.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, await link.Content.ReadAsStringAsync());
        await (await manager.GetAsync($"/api/me/pay/payslips/{payslipId}")).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        var list = await ExpectAsync(await manager.GetAsync($"/api/me/pay/payslips?year={month.Year}"), HttpStatusCode.OK);
        var attendance = await ExpectAsync(await manager.GetAsync($"/api/me/pay/attendance?from={month.Start:yyyy-MM-dd}&to={month.End:yyyy-MM-dd}"), HttpStatusCode.OK);
        var advances = await ExpectAsync(await manager.GetAsync("/api/me/pay/advances"), HttpStatusCode.OK);
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Empty(attendance.GetProperty("items").EnumerateArray());
        Assert.Empty(advances.GetProperty("items").EnumerateArray());
        await (await manager.GetAsync($"/api/payrolls/{month.Key}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
    }
}
