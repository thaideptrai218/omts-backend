using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-02 and BR-LABOR-03: assigning work to employees on a raft, editing, removing, moving and changing the job of a crew.</summary>
public class WorkAssignmentTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_manager_assigns_employees_a_job_on_a_raft_and_the_day_board_lists_them()
    {
        var s = await SetupWorkAsync(factory);
        var (a, b) = (await AddEmployeeAsync(s, "An"), await AddEmployeeAsync(s, "Bình"));

        var rows = await AssignTodayAsync(s, s.Raft, s.Harvest, a, b);

        Assert.Equal(2, rows.GetArrayLength());
        var row = rows.EnumerateArray().Single(r => r.GetProperty("employeeId").GetGuid() == a);
        Assert.Equal(("An", s.Raft.Code, s.Raft.Id, s.Harvest, s.Zone.Id, Day(Today())),
            (row.Str("employeeName"), row.Str("raftCode"), row.GetProperty("raftId").GetGuid(), row.GetProperty("jobTypeId").GetGuid(), row.GetProperty("zoneId").GetGuid(), row.Str("date")));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("checkedInAt").ValueKind);
        Assert.False(row.GetProperty("complete").GetBoolean());
        var board = await s.Manager.GetFromJsonAsync<JsonElement>($"{Workforce}/assignments?day={Day(Today())}");
        Assert.Contains(board.EnumerateArray(), r => r.GetProperty("employeeId").GetGuid() == b);
        var ids = rows.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "WorkAssignment.Created" && e.RecordId != null && ids.Contains(e.RecordId.Value) && e.ZoneId == s.Zone.Id)));
    }

    [Fact]
    public async Task The_zone_of_the_raft_joins_the_working_zones_of_an_employee_who_does_not_have_it()
    {
        var s = await SetupWorkAsync(factory);
        var farAway = await AddEmployeeAsync(s, zone: s.OtherZone);

        await AssignTodayAsync(s, s.Raft, s.Harvest, farAway);

        var zones = (await factory.QueryAsync(db => db.EmployeeWorkingZones.Where(w => w.EmployeeId == farAway).Select(w => w.ZoneId).ToListAsync()));
        Assert.Contains(s.Zone.Id, zones);
        Assert.Contains(s.OtherZone.Id, zones);
    }

    [Fact]
    public async Task A_date_in_the_past_a_discontinued_job_type_and_bad_lists_are_refused_with_the_field_named()
    {
        var s = await SetupWorkAsync(factory);
        var a = await AddEmployeeAsync(s);

        var past = await AssignAsync(s.Manager, s.Raft.Id, s.Harvest, Today().AddDays(-1), false, a);
        Assert.Equal(("MSG-GEN-01", "Date (today or later)"), (await past.ErrorCodeAsync(), await FieldAsync(past)));

        await s.Factory.UpdateAsync<OMTS.Domain.Entities.Labor.JobType>(s.Hanging, j => j.StatusId = StatusIds.Usage.Discontinued);
        var discontinued = await AssignAsync(s.Manager, s.Raft.Id, s.Hanging, Today(), false, a);
        Assert.Equal(("MSG-GEN-09", "Job type"), (await discontinued.ErrorCodeAsync(), await FieldAsync(discontinued)));

        var none = await AssignAsync(s.Manager, s.Raft.Id, s.Harvest, Today(), false);
        Assert.Equal("Employees", await FieldAsync(none));
        var repeated = await AssignAsync(s.Manager, s.Raft.Id, s.Harvest, Today(), false, a, a);
        Assert.Equal("Employees", await FieldAsync(repeated));
        var unknown = await AssignAsync(s.Manager, s.Raft.Id, s.Harvest, Today(), false, Guid.NewGuid());
        Assert.Equal("Employees", await FieldAsync(unknown));
    }

    [Fact]
    public async Task A_left_employee_cannot_be_assigned()
    {
        var s = await SetupWorkAsync(factory);
        var gone = await AddEmployeeAsync(s);
        await EndAsync(s.Owner, gone);

        var response = await AssignAsync(s.Manager, s.Raft.Id, s.Harvest, Today(), false, gone);

        Assert.Equal(("MSG-GEN-09", "Employees"), (await response.ErrorCodeAsync(), await FieldAsync(response)));
    }

    [Fact]
    public async Task A_manager_assigns_only_on_the_rafts_of_their_zones_and_the_owner_on_every_raft()
    {
        var s = await SetupWorkAsync(factory);
        var a = await AddEmployeeAsync(s);

        var refused = await AssignAsync(s.Manager, s.OtherRaft.Id, s.Harvest, Today(), false, a);
        Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());

        await (await AssignAsync(s.Owner, s.OtherRaft.Id, s.Harvest, Today(), false, a)).BodyAsync(HttpStatusCode.Created);
        // The board of the manager shows only the rafts of their zones.
        var board = await s.Manager.GetFromJsonAsync<JsonElement>($"{Workforce}/assignments?day={Day(Today())}");
        Assert.DoesNotContain(board.EnumerateArray(), r => r.GetProperty("employeeId").GetGuid() == a);
        var ownerBoard = await s.Owner.GetFromJsonAsync<JsonElement>($"{Workforce}/assignments?day={Day(Today())}");
        Assert.Contains(ownerBoard.EnumerateArray(), r => r.GetProperty("employeeId").GetGuid() == a);
    }

    [Fact]
    public async Task An_employee_on_another_raft_is_moved_only_after_the_manager_agrees()
    {
        var s = await SetupWorkAsync(factory);
        var a = await AddEmployeeAsync(s, "Một");
        await AssignTodayAsync(s, s.Raft, s.Harvest, a);

        var asked = await AssignAsync(s.Manager, s.Raft2.Id, s.Hanging, Today(), false, a);

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var problem = await ProblemAsync(asked);
        Assert.Equal("MSG-LABOR-06", problem.Str("code"));
        Assert.Contains(s.Raft.Code, problem.Str("detail"));
        Assert.Equal(a, problem.GetProperty("employeeIds")[0].GetGuid());
        Assert.Equal(s.Raft.Id, (await RowOfAsync(s.Manager, a)).GetProperty("raftId").GetGuid());

        var moved = await (await AssignAsync(s.Manager, s.Raft2.Id, s.Hanging, Today(), true, a)).BodyAsync(HttpStatusCode.Created);

        Assert.Equal((s.Raft2.Id, s.Hanging), (moved[0].GetProperty("raftId").GetGuid(), moved[0].GetProperty("jobTypeId").GetGuid()));
        Assert.Equal(1, await factory.QueryAsync(db => db.WorkAssignments.CountAsync(w => w.EmployeeId == a && w.Date == Today())));
    }

    [Fact]
    public async Task The_same_employee_assigned_again_on_the_same_raft_changes_only_the_job()
    {
        var s = await SetupWorkAsync(factory);
        var a = await AddEmployeeAsync(s);
        await AssignTodayAsync(s, s.Raft, s.Harvest, a);

        var again = await (await AssignAsync(s.Manager, s.Raft.Id, s.Hanging, Today(), false, a)).BodyAsync(HttpStatusCode.Created);

        Assert.Equal(s.Hanging, again[0].GetProperty("jobTypeId").GetGuid());
        Assert.Equal(1, await factory.QueryAsync(db => db.WorkAssignments.CountAsync(w => w.EmployeeId == a && w.Date == Today())));
    }

    [Fact]
    public async Task An_employee_already_checked_in_is_moved_only_with_change_job_for_crew()
    {
        var s = await SetupWorkAsync(factory);
        var a = await AddEmployeeAsync(s, "Hai");
        var rows = await AssignTodayAsync(s, s.Raft, s.Harvest, a);
        await (await CheckInAsync(s.Manager, RowId(rows, a))).BodyAsync(HttpStatusCode.OK);

        var response = await AssignAsync(s.Manager, s.Raft2.Id, s.Hanging, Today(), true, a);

        Assert.Equal(("MSG-LABOR-07", HttpStatusCode.Conflict), (await response.ErrorCodeAsync(), response.StatusCode));
    }

    [Fact]
    public async Task An_assignment_is_edited_and_removed_until_the_employee_is_checked_in()
    {
        var s = await SetupWorkAsync(factory);
        var (a, b) = (await AddEmployeeAsync(s), await AddEmployeeAsync(s));
        var rows = await AssignTodayAsync(s, s.Raft, s.Harvest, a, b);
        var (idA, idB) = (RowId(rows, a), RowId(rows, b));

        var edited = await (await s.Manager.PutAsJsonAsync($"{Workforce}/assignments/{idA}", new { raftId = s.Raft2.Id, jobTypeId = s.Hanging })).BodyAsync(HttpStatusCode.OK);
        Assert.Equal((idA, s.Raft2.Code, s.Hanging), (edited.GetProperty("id").GetGuid(), edited.Str("raftCode"), edited.GetProperty("jobTypeId").GetGuid()));

        var removed = await s.Manager.DeleteAsync($"{Workforce}/assignments/{idB}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(w => w.Id == idB)));

        await (await CheckInAsync(s.Manager, idA)).BodyAsync(HttpStatusCode.OK);
        var afterCheckIn = await s.Manager.PutAsJsonAsync($"{Workforce}/assignments/{idA}", new { raftId = s.Raft.Id, jobTypeId = s.Harvest });
        Assert.Equal("MSG-LABOR-07", await afterCheckIn.ErrorCodeAsync());
        Assert.Equal("MSG-LABOR-07", await (await s.Manager.DeleteAsync($"{Workforce}/assignments/{idA}")).ErrorCodeAsync());
        Assert.True(await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(w => w.Id == idA)));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Manager.DeleteAsync($"{Workforce}/assignments/{idB}")).StatusCode);
    }

    [Fact]
    public async Task Change_job_for_crew_adds_a_job_period_for_those_checked_in_and_changes_the_assignment_of_the_others()
    {
        var s = await SetupWorkAsync(factory);
        var (inside, waiting) = (await AddEmployeeAsync(s), await AddEmployeeAsync(s));
        var rows = await AssignTodayAsync(s, s.Raft, s.Harvest, inside, waiting);
        await (await CheckInAsync(s.Manager, RowId(rows, inside))).BodyAsync(HttpStatusCode.OK);

        var response = await s.Manager.PostAsJsonAsync($"{Workforce}/crew-job", new { raftId = s.Raft2.Id, jobTypeId = s.Hanging, employeeIds = new[] { inside, waiting } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var periods = await factory.QueryAsync(db => db.WorkAssignments.Where(w => w.EmployeeId == inside).OrderBy(w => w.StartsAt).ToListAsync());
        Assert.Equal(2, periods.Count);
        Assert.Null(periods[0].StartsAt);
        Assert.Equal((s.Harvest, s.Raft.Id), (periods[0].JobTypeId, periods[0].RaftId));
        Assert.Equal((s.Hanging, s.Raft2.Id), (periods[1].JobTypeId, periods[1].RaftId));
        Assert.NotNull(periods[1].StartsAt);
        var other = await factory.QueryAsync(db => db.WorkAssignments.SingleAsync(w => w.EmployeeId == waiting));
        Assert.Equal((s.Hanging, s.Raft2.Id, (DateTimeOffset?)null), (other.JobTypeId, other.RaftId, other.StartsAt));
        // The row of the day keeps the id of the first period and shows the current job.
        var row = await RowOfAsync(s.Manager, inside);
        Assert.Equal((RowId(rows, inside), s.Hanging, s.Raft2.Code), (row.GetProperty("id").GetGuid(), row.GetProperty("jobTypeId").GetGuid(), row.Str("raftCode")));
        Assert.Equal(2, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "WorkAssignment.JobChanged" && (e.RecordId == periods[0].Id || e.RecordId == other.Id))));
    }

    [Fact]
    public async Task Change_job_for_crew_refuses_someone_without_work_today_or_already_checked_out()
    {
        var s = await SetupWorkAsync(factory);
        var (stranger, done) = (await AddEmployeeAsync(s), await AddEmployeeAsync(s));
        var rows = await AssignTodayAsync(s, s.Raft, s.Harvest, done);
        var id = RowId(rows, done);
        await (await CheckInAsync(s.Manager, id)).BodyAsync(HttpStatusCode.OK);
        await (await CheckOutAsync(s.Manager, id)).BodyAsync(HttpStatusCode.OK);

        foreach (var member in new[] { stranger, done })
        {
            var response = await s.Manager.PostAsJsonAsync($"{Workforce}/crew-job", new { raftId = s.Raft2.Id, jobTypeId = s.Hanging, employeeIds = new[] { member } });
            Assert.Equal(("MSG-GEN-01", "Employees"), (await response.ErrorCodeAsync(), await FieldAsync(response)));
        }
    }

    [Fact]
    public async Task The_lookups_list_the_jobs_in_use_and_the_employees_of_the_zones_with_their_leave_and_raft()
    {
        var s = await SetupWorkAsync(factory);
        var (onRaft, free, foreign) = (await AddEmployeeAsync(s, "Có bè"), await AddEmployeeAsync(s, "Rảnh"), await AddEmployeeAsync(s, "Khác vùng", zone: s.OtherZone));
        await AssignTodayAsync(s, s.Raft, s.Harvest, onRaft);
        await s.Factory.UseDbAsync(db => db.EmployeeRequests.Add(new OMTS.Domain.Entities.Labor.EmployeeRequest
        {
            EmployeeId = free, Type = EmployeeRequestType.Leave, FromDate = Today(), ToDate = Today(), Reason = "Nghỉ", SentAt = DateTimeOffset.UtcNow, StatusId = StatusIds.EmployeeRequest.Approved,
        }));

        var lookups = await s.Manager.GetFromJsonAsync<JsonElement>($"{Workforce}/lookups?day={Day(Today())}");

        Assert.Contains(lookups.GetProperty("jobs").EnumerateArray(), j => j.GetProperty("id").GetGuid() == s.Harvest);
        var people = lookups.GetProperty("employees").EnumerateArray().ToList();
        Assert.Equal(s.Raft.Code, people.Single(p => p.GetProperty("id").GetGuid() == onRaft).Str("raftCode"));
        Assert.True(people.Single(p => p.GetProperty("id").GetGuid() == free).GetProperty("onLeave").GetBoolean());
        Assert.False(people.Single(p => p.GetProperty("id").GetGuid() == onRaft).GetProperty("onLeave").GetBoolean());
        Assert.DoesNotContain(people, p => p.GetProperty("id").GetGuid() == foreign);
    }

    [Fact]
    public async Task Accountants_traders_and_anonymous_callers_are_refused_on_every_assignment_endpoint()
    {
        var s = await SetupWorkAsync(factory);
        var a = await AddEmployeeAsync(s);
        var id = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, a), a);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var calls = new Func<HttpClient, Task<HttpResponseMessage>>[]
        {
            c => c.GetAsync($"{Workforce}/assignments"),
            c => c.GetAsync($"{Workforce}/lookups"),
            c => AssignAsync(c, s.Raft.Id, s.Harvest, Today(), false, a),
            c => c.PutAsJsonAsync($"{Workforce}/assignments/{id}", new { raftId = s.Raft.Id, jobTypeId = s.Harvest }),
            c => c.DeleteAsync($"{Workforce}/assignments/{id}"),
            c => c.PostAsJsonAsync($"{Workforce}/crew-job", new { raftId = s.Raft.Id, jobTypeId = s.Harvest, employeeIds = new[] { a } }),
        };

        foreach (var client in new[] { await ManagerAsync(factory, null, StaffRole.Accountant), await factory.LoginAsync(trader.Username) })
        {
            foreach (var call in calls)
            {
                Assert.Equal("MSG-GEN-08", await (await call(client)).ErrorCodeAsync());
            }
        }

        foreach (var call in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await call(factory.CreateClient())).StatusCode);
        }

        // A manager of another zone cannot reach the assignment of this one.
        var otherManager = await ManagerAsync(factory, s.OtherZone);
        Assert.Equal("MSG-GEN-08", await (await otherManager.DeleteAsync($"{Workforce}/assignments/{id}")).ErrorCodeAsync());
        Assert.True(await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(w => w.Id == id)));
    }

    [Fact]
    public async Task Ending_the_employment_removes_the_assignments_after_the_last_working_day_and_keeps_the_attendance()
    {
        var s = await SetupWorkAsync(factory);
        var a = await AddEmployeeAsync(s);
        await AssignAsync(s.Manager, s.Raft.Id, s.Harvest, Today(), false, a);
        await AssignAsync(s.Manager, s.Raft.Id, s.Harvest, Today().AddDays(2), false, a);

        var ended = await s.Owner.PostAsJsonAsync($"/api/employees/{a}/end-employment", await EndBodyAsync(s.Owner, a, Today()));

        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        var days = await factory.QueryAsync(db => db.WorkAssignments.Where(w => w.EmployeeId == a).Select(w => w.Date).ToListAsync());
        Assert.Equal([Today()], days);
    }

    [Fact]
    public async Task The_last_working_day_cannot_be_before_a_day_with_attendance()
    {
        var s = await SetupWorkAsync(factory);
        var a = await AddEmployeeAsync(s);
        var day = Today().AddDays(-3);
        await AddPastDayAsync(s, a, day, "06:00", "16:00", (s.Harvest, s.Raft.Id, null));

        var ended = await s.Owner.PostAsJsonAsync($"/api/employees/{a}/end-employment", await EndBodyAsync(s.Owner, a, day.AddDays(-1)));

        Assert.Equal("MSG-GEN-01", await ended.ErrorCodeAsync());
    }
}
