using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-08 and BR-LABOR-09: the employee linked to an account sees only their own assignments, attendance with hours, and requests.</summary>
public class MyWorkTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly DateOnly PastDay = new(2026, 3, 2);

    private static string Range(DateOnly from, DateOnly to) => $"{Me}/workforce?from={Day(from)}&to={Day(to)}";

    [Fact]
    public async Task An_account_without_an_employee_profile_sees_nothing()
    {
        var s = await SetupWorkAsync(factory);

        var work = await s.Manager.GetFromJsonAsync<JsonElement>(Range(Today(), Today()));

        Assert.Equal(JsonValueKind.Null, work.GetProperty("employeeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, work.GetProperty("name").ValueKind);
        Assert.All(new[] { "assignments", "attendance", "requests" }, p => Assert.Equal(0, work.GetProperty(p).GetArrayLength()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Range(Today(), Today()))).StatusCode);
    }

    [Fact]
    public async Task The_period_is_at_most_62_days_and_ends_after_it_starts()
    {
        var s = await SetupWorkAsync(factory);

        Assert.Equal("Period", await FieldAsync(await s.Manager.GetAsync(Range(Today(), Today().AddDays(-1)))));
        Assert.Equal("Period", await FieldAsync(await s.Manager.GetAsync(Range(Today(), Today().AddDays(62)))));
        Assert.Equal(HttpStatusCode.OK, (await s.Manager.GetAsync(Range(Today(), Today().AddDays(61)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.GetAsync($"{Me}/workforce")).StatusCode);
    }

    [Fact]
    public async Task A_seasonal_worker_sees_assignments_attendance_days_with_hours_and_requests_but_not_those_of_others()
    {
        await using var own = new AuthApiFactory();
        var s = await SetupWorkAsync(own);
        var settings = await s.Owner.GetFromJsonAsync<JsonElement>(AttendanceSettings);
        await (await s.Owner.PostAsJsonAsync(AttendanceSettings, new { breakStart = "11:30", breakEnd = "12:30", roundingUnit = "Minutes15", effectiveFrom = "2020-01-01", rowVersion = settings.VersionOf() })).BodyAsync(HttpStatusCode.OK);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var stranger = await AddEmployeeAsync(s);
        await AddPastDayAsync(s, employee, PastDay, "06:10", "16:55", (s.Hanging, s.Raft.Id, null), (s.Harvest, s.Raft.Id, "09:40"));
        await AddPastDayAsync(s, employee, PastDay.AddDays(1), "06:00", null, (s.Harvest, s.Raft.Id, null));
        await AddPastDayAsync(s, stranger, PastDay, "06:00", "10:00", (s.Harvest, s.Raft.Id, null));
        var today = await AssignTodayAsync(s, s.Raft2, s.Harvest, employee);
        var request = await (await mine.PostAsJsonAsync($"{Me}/leave-requests", new { from = Day(Today().AddDays(3)), to = Day(Today().AddDays(3)), reason = "Việc nhà", clientOperationId = Guid.NewGuid() })).BodyAsync(HttpStatusCode.Created);

        var work = await mine.GetFromJsonAsync<JsonElement>(Range(PastDay, PastDay.AddDays(1)));
        var todayWork = await mine.GetFromJsonAsync<JsonElement>(Range(Today(), Today()));

        Assert.Equal(employee, work.GetProperty("employeeId").GetGuid());
        var days = work.GetProperty("attendance").EnumerateArray().ToList();
        Assert.Equal([Day(PastDay), Day(PastDay.AddDays(1))], days.Select(d => d.Str("date")));
        var complete = days[0];
        Assert.True(complete.GetProperty("complete").GetBoolean());
        Assert.Equal((3.5m, 6.25m), (complete.GetProperty("jobs")[0].GetProperty("hours").GetDecimal(), complete.GetProperty("jobs")[1].GetProperty("hours").GetDecimal()));
        var periods = complete.GetProperty("periods").EnumerateArray().ToList();
        Assert.Equal((2, s.Raft.Code, 210, 375), (periods.Count, periods[0].Str("raft"), periods[0].GetProperty("minutes").GetInt32(), periods[1].GetProperty("minutes").GetInt32()));
        // A day with no check-out counts no hours and shows no periods (BR-LABOR-06).
        Assert.False(days[1].GetProperty("complete").GetBoolean());
        Assert.Equal(0, days[1].GetProperty("periods").GetArrayLength());
        Assert.Equal(0, days[1].GetProperty("jobs").GetArrayLength());
        var rows = work.GetProperty("assignments").EnumerateArray().Concat(todayWork.GetProperty("assignments").EnumerateArray()).ToList();
        Assert.Contains(rows, r => r.GetProperty("id").GetGuid() == RowId(today, employee) && r.Str("raftCode") == s.Raft2.Code);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(employee, r.GetProperty("employeeId").GetGuid()));
        Assert.Equal([request.GetProperty("id").GetGuid()], work.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_staff_employee_sees_days_worked_and_no_hours()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s, staff: true);
        await AddPastDayAsync(s, employee, PastDay, "07:00", "16:00");

        var work = await mine.GetFromJsonAsync<JsonElement>(Range(PastDay, PastDay));

        var day = work.GetProperty("attendance")[0];
        Assert.True(day.GetProperty("complete").GetBoolean());
        Assert.Equal((0, 0), (day.GetProperty("periods").GetArrayLength(), day.GetProperty("jobs").GetArrayLength()));
    }

    [Fact]
    public async Task Requests_still_awaiting_review_are_shown_whatever_the_period()
    {
        var s = await SetupWorkAsync(factory);
        var (_, mine) = await AddLinkedEmployeeAsync(s);
        var future = await (await mine.PostAsJsonAsync($"{Me}/leave-requests", new { from = Day(Today().AddDays(30)), to = Day(Today().AddDays(31)), reason = "Về quê", clientOperationId = Guid.NewGuid() })).BodyAsync(HttpStatusCode.Created);

        var work = await mine.GetFromJsonAsync<JsonElement>(Range(PastDay, PastDay));

        Assert.Equal([future.GetProperty("id").GetGuid()], work.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
    }
}
