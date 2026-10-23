using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// QA of the PR #13 review fixes: a team row needs a member version only for the members it changes, a Left employee
/// is assignable until the end date (MSG72 from that day), a suggested worker row keeps its team after the query change,
/// and a bulk pay rate into a finalized month is 409 PERIOD_LOCKED for the whole batch.
/// </summary>
public class ReviewFixQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string PayRates = "/api/pay-rates";

    private static string Sheet(LaborWorld w, DateOnly date) => $"{w.Attendance}/{date:yyyy-MM-dd}";

    private Task<AttendanceResult> ResultOfAsync(Guid workerId, DateOnly date, Guid zoneId) =>
        factory.QueryAsync(db => db.AttendanceRecords.AsNoTracking()
            .Where(r => r.WorkerId == workerId && r.WorkDate == date && r.ZoneId == zoneId).Select(r => r.Result).SingleAsync());

    [Fact]
    public async Task A_team_row_needs_a_member_version_only_for_the_members_it_actually_changes()
    {
        var w = await factory.WorldAsync();
        var team = await w.AddTeamAsync();
        var changed = await w.AddWorkerAsync(team.Id);
        var unchanged = await w.AddWorkerAsync(team.Id);
        var date = NewMonth().Day(14);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { teamId = team.Id, result = "Present" } } }), HttpStatusCode.OK);

        // Only the first member changes away from Present; the second keeps its saved Present record.
        var ownVersion = await w.AttendanceVersionAsync(date, changed.Id);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new
        {
            rows = new[] { new { workerId = changed.Id, result = "UnpaidLeave", reason = "Nghỉ không lương", rowVersion = ownVersion } },
        }), HttpStatusCode.OK);

        object teamRow = new { teamId = team.Id, result = "Present", reason = "Sửa về có mặt" };
        var missing = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { teamRow } })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        Assert.Equal("RowVersion", missing.Field());
        Assert.Equal(AttendanceResult.UnpaidLeave, await ResultOfAsync(changed.Id, date, w.Zone.Id));

        var versions = new Dictionary<Guid, string> { [changed.Id] = await w.AttendanceVersionAsync(date, changed.Id) };
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { teamRow }, memberRowVersions = versions }), HttpStatusCode.OK);

        Assert.Equal(AttendanceResult.Present, await ResultOfAsync(changed.Id, date, w.Zone.Id));
        Assert.Equal(AttendanceResult.Present, await ResultOfAsync(unchanged.Id, date, w.Zone.Id));
    }

    [Fact]
    public async Task A_Left_employee_is_assignable_until_the_end_date_and_the_end_date_itself_is_MSG72()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(15);
        var stillEmployed = await w.AddEmployeeAsync(configure: e => (e.StatusId, e.EmploymentEndDate) = (StatusIds.Employee.Left, date.AddDays(1)));
        var ended = await w.AddEmployeeAsync(configure: e => (e.StatusId, e.EmploymentEndDate) = (StatusIds.Employee.Left, date));

        await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, new { workDate = date, employeeId = stillEmployed.Id, task = "Kéo bè", paymentType = "DailyWage" }), HttpStatusCode.Created);
        await (await w.Owner.PostAsJsonAsync(w.Assignments, new { workDate = date, employeeId = ended.Id, task = "Kéo bè", paymentType = "DailyWage" }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG72");
    }

    [Fact]
    public async Task A_suggested_worker_row_still_shows_its_team_after_the_one_query_change()
    {
        var w = await factory.WorldAsync();
        var team = await w.AddTeamAsync();
        var worker = await w.AddWorkerAsync(team.Id);
        var date = NewMonth().Day(16);
        await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, new { workDate = date, workerId = worker.Id, task = "Kéo bè", paymentType = "DailyWage" }), HttpStatusCode.Created);

        var sheet = await ExpectAsync(await w.Owner.GetAsync($"{w.Attendance}?date={date:yyyy-MM-dd}"), HttpStatusCode.OK);
        var row = sheet.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("personId").GetGuid() == worker.Id);

        Assert.True(row.GetProperty("suggested").GetBoolean());
        Assert.Equal(team.Id, row.GetProperty("team").GetProperty("id").GetGuid());
        Assert.Equal(team.Name, row.GetProperty("team").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_bulk_rate_into_a_finalized_month_is_409_PERIOD_LOCKED_for_every_person_and_saves_none()
    {
        var w = await factory.WorldAsync();
        var first = await w.AddEmployeeAsync();
        var second = await w.AddEmployeeAsync();
        var finalized = await w.FinalizeMonthAsync(NewMonth());

        await (await w.Owner.PostAsJsonAsync($"{PayRates}/bulk", new
        {
            people = new object[] { new { employeeId = (Guid?)first.Id }, new { employeeId = (Guid?)second.Id } },
            rate = new { salaryType = "MonthlyFixed", amount = 5_000_000, validFrom = finalized.Day(2) },
        })).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");

        Assert.Equal(0, await factory.QueryAsync(db => db.PayRuleVersions.CountAsync(v => v.EmployeeId == first.Id || v.EmployeeId == second.Id)));
    }
}
