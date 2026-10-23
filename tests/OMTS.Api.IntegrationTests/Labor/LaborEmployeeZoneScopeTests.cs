using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-03/LAB-04 (review): the pickers list only Working employees of the zone, so the save paths enforce the same rule.
/// Not Working or not in the zone → 400 MSG01 "Employee" (the Owner is not exempt); MSG72 still wins for a Left employee.
/// </summary>
public class LaborEmployeeZoneScopeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static object Daily(DateOnly date, Guid employeeId, string task = "Vệ sinh lồng", string? rowVersion = null) =>
        new { workDate = date, employeeId, task, paymentType = "DailyWage", rowVersion };

    [Fact]
    public async Task Assigning_an_employee_of_another_zone_is_MSG01_even_for_the_Owner()
    {
        var w = await factory.WorldAsync();
        var elsewhere = await w.AddEmployeeAsync(inZone: false);
        var date = NewMonth().Day(10);

        var problem = await (await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, elsewhere.Id))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal("Employee", problem.Field());
        Assert.Equal(0, await factory.QueryAsync(db => db.WorkAssignments.CountAsync(a => a.EmployeeId == elsewhere.Id)));
    }

    [Fact]
    public async Task Employee_who_left_without_an_end_date_cannot_be_assigned_and_MSG72_still_wins_over_the_zone_rule()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(10);
        var noEndDate = await w.AddEmployeeAsync(configure: e => e.StatusId = StatusIds.Employee.Left);
        var leftOutsideZone = await w.AddEmployeeAsync(inZone: false, configure: e => (e.StatusId, e.EmploymentEndDate) = (StatusIds.Employee.Left, date.AddDays(-1)));

        var left = await (await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, noEndDate.Id))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        await (await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, leftOutsideZone.Id))).ProblemAsync(HttpStatusCode.Conflict, "MSG72");

        Assert.Equal("Employee", left.Field());
    }

    [Fact]
    public async Task Changing_the_assignee_of_an_edit_to_an_employee_of_another_zone_is_MSG01_an_unchanged_assignee_stays_editable()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(10);
        var inside = await w.AddEmployeeAsync();
        var elsewhere = await w.AddEmployeeAsync(inZone: false);
        var created = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, inside.Id)), HttpStatusCode.Created);
        var url = $"{w.Assignments}/{created.GetProperty("id").GetGuid()}";
        var rowVersion = created.GetProperty("rowVersion").GetString();

        var moved = await (await w.Owner.PutAsJsonAsync(url, Daily(date, elsewhere.Id, rowVersion: rowVersion))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        // The employee was moved out of the zone after the assignment was made: the same assignee is not re-checked.
        await factory.UseDbAsync(db => db.EmployeeWorkingZones.RemoveRange(db.EmployeeWorkingZones.Where(z => z.EmployeeId == inside.Id)));
        var renamed = await ExpectAsync(await w.Owner.PutAsJsonAsync(url, Daily(date, inside.Id, "Vệ sinh lưới", rowVersion)), HttpStatusCode.OK);

        Assert.Equal("Employee", moved.Field());
        Assert.Equal("Vệ sinh lưới", renamed.GetProperty("task").GetString());
    }

    [Fact]
    public async Task Attendance_of_an_employee_of_another_zone_or_who_is_not_employed_is_MSG01_all_or_nothing()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(12);
        var inside = await w.AddEmployeeAsync();
        var elsewhere = await w.AddEmployeeAsync(inZone: false);
        var noEndDate = await w.AddEmployeeAsync(configure: e => e.StatusId = StatusIds.Employee.Left);
        var sheet = $"{w.Attendance}/{date:yyyy-MM-dd}";

        var outside = await (await w.Owner.PutAsJsonAsync(sheet, new
        {
            rows = new[] { new { employeeId = inside.Id, result = "Present" }, new { employeeId = elsewhere.Id, result = "Present" } },
        })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var left = await (await w.Owner.PutAsJsonAsync(sheet, new { rows = new[] { new { employeeId = noEndDate.Id, result = "Present" } } })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        await ExpectAsync(await w.Owner.PutAsJsonAsync(sheet, new { rows = new[] { new { employeeId = inside.Id, result = "Present" } } }), HttpStatusCode.OK);

        Assert.Equal(("Employee", 1), (outside.Field(), outside.GetProperty("rows")[0].GetProperty("index").GetInt32()));
        Assert.Equal("Employee", left.Field());
        Assert.Equal([inside.Id], await factory.QueryAsync(db => db.AttendanceRecords.Where(r => r.WorkDate == date && r.ZoneId == w.Zone.Id).Select(r => r.EmployeeId!.Value).ToListAsync()));
    }
}
