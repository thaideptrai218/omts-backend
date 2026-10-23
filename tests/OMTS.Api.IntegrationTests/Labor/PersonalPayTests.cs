using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-10 personal pay: own data only, no Draft figures, MSG51 for an unlinked account, MSG08 for an empty range.</summary>
public class PersonalPayTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Account_not_linked_to_an_employee_is_MSG51()
    {
        var w = await factory.WorldAsync();
        var (staff, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.Customers, true, false, false, false));

        var problem = await (await staff.GetAsync("/api/me/pay/payslips")).ProblemAsync(HttpStatusCode.Conflict, "MSG51");
        await (await w.Owner.GetAsync("/api/me/pay/attendance")).ProblemAsync(HttpStatusCode.Conflict, "MSG51");

        Assert.Equal("The account is not clearly linked to an employee profile, so personal information cannot be displayed yet.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task No_data_in_the_range_is_an_empty_list_with_MSG08()
    {
        var w = await factory.WorldAsync();
        var (_, client, _) = await w.AddEmployeeWithAccountAsync();

        var attendance = await ExpectAsync(await client.GetAsync("/api/me/pay/attendance?from=2020-01-01&to=2020-01-31"), HttpStatusCode.OK);
        var tooLong = await client.GetAsync("/api/me/pay/attendance?from=2020-01-01&to=2021-06-30");

        Assert.Equal(0, attendance.GetProperty("items").GetArrayLength());
        Assert.Equal("There is no data for attendance in the selected scope.", attendance.GetProperty("message").GetString());
        await tooLong.ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
    }

    [Fact]
    public async Task Staff_see_only_their_own_finalized_payslips_and_attendance_never_drafts_or_others()
    {
        var w = await factory.WorldAsync();
        var (me, client, _) = await w.AddEmployeeWithAccountAsync();
        var colleague = await w.AddEmployeeAsync();
        var finalizedMonth = NewMonth();
        var draftMonth = NewMonth();
        foreach (var employee in new[] { me, colleague })
        {
            await w.AddRateAsync(finalizedMonth, employee.Id, null, SalaryType.DailyWage, 300_000);
            await w.AddRateAsync(draftMonth, employee.Id, null, SalaryType.DailyWage, 300_000);
            await w.AddAttendanceAsync(finalizedMonth.Day(3), employee.Id, null);
            await w.AddAttendanceAsync(draftMonth.Day(4), employee.Id, null);
        }

        var finalized = await w.CreatePayrollAsync(finalizedMonth);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{finalizedMonth.Key}/finalize", null), HttpStatusCode.OK);
        var draft = await w.CreatePayrollAsync(draftMonth);
        var mine = finalized.PayslipOf(me.Id).GetProperty("id").GetGuid();
        var theirs = finalized.PayslipOf(colleague.Id).GetProperty("id").GetGuid();
        var myDraft = draft.PayslipOf(me.Id).GetProperty("id").GetGuid();

        var list = await ExpectAsync(await client.GetAsync($"/api/me/pay/payslips?year={finalizedMonth.Year}"), HttpStatusCode.OK);
        var draftYear = draftMonth.Year == finalizedMonth.Year ? list : await ExpectAsync(await client.GetAsync($"/api/me/pay/payslips?year={draftMonth.Year}"), HttpStatusCode.OK);
        var detail = await ExpectAsync(await client.GetAsync($"/api/me/pay/payslips/{mine}"), HttpStatusCode.OK);
        var other = await client.GetAsync($"/api/me/pay/payslips/{theirs}");
        var draftDetail = await client.GetAsync($"/api/me/pay/payslips/{myDraft}");
        var attendance = await ExpectAsync(await client.GetAsync(
            $"/api/me/pay/attendance?from={draftMonth.Start:yyyy-MM-dd}&to={finalizedMonth.End:yyyy-MM-dd}"), HttpStatusCode.OK);

        Assert.Equal([mine], list.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(draftYear.GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == myDraft);
        Assert.Equal((300_000m, 300_000m), (detail.GetProperty("net").GetDecimal(), detail.GetProperty("remaining").GetDecimal()));
        await other.ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await draftDetail.ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(new[] { draftMonth.Day(4), finalizedMonth.Day(3) }.Order().Select(d => d.ToString("yyyy-MM-dd")),
            attendance.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("date").GetString()));
        Assert.All(attendance.GetProperty("items").EnumerateArray(), a => Assert.Equal(w.Zone.Name, a.GetProperty("zoneName").GetString()));
    }

    [Fact]
    public async Task Own_advances_are_listed_with_what_is_still_outstanding()
    {
        var w = await factory.WorldAsync();
        var (me, client, _) = await w.AddEmployeeWithAccountAsync();
        var colleague = await w.AddEmployeeAsync();
        var day = NewMonth().Day(5);
        await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances", new { employeeId = me.Id, amount = 300_000, paymentDate = day, method = "Cash" }), HttpStatusCode.Created);
        await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances", new { employeeId = colleague.Id, amount = 400_000, paymentDate = day, method = "Cash" }), HttpStatusCode.Created);

        var advances = await ExpectAsync(await client.GetAsync("/api/me/pay/advances"), HttpStatusCode.OK);

        var advance = Assert.Single(advances.GetProperty("items").EnumerateArray());
        Assert.Equal((me.Id, 300_000m), (advance.GetProperty("person").GetProperty("id").GetGuid(), advance.GetProperty("remaining").GetDecimal()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, advances.GetProperty("message").ValueKind);
    }
}
