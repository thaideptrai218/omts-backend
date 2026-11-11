using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-02.0 step 1 (SCR-LABOR-02): the work day of the caller's zones: rafts In Use with their crews, the Staff list and the hours of a complete day.</summary>
public class WorkDayViewTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    private static JsonElement RaftOf(JsonElement day, Raft raft) =>
        day.GetProperty("rafts").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == raft.Id);

    [Fact]
    public async Task A_zone_manager_sees_the_rafts_in_use_of_their_zones_with_the_crews()
    {
        var (zone, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, empty, discontinued, away) = (await AddRaftAsync(host, zone), await AddRaftAsync(host, zone), await AddRaftAsync(host, zone, StatusIds.Usage.Discontinued), await AddRaftAsync(host, zoneB));
        var job = await AddJobTypeAsync(host);
        var (first, second, elsewhere) = (await AddEmployeeAsync(host, [zone], change: e => e.FullName = $"A {NewCode()}"), await AddEmployeeAsync(host, [zone], change: e => e.FullName = $"B {NewCode()}"), await AddEmployeeAsync(host, [zoneB]));
        await manager.AssignAsync(raft, Today(), job, [second, first]).BodyAsync(HttpStatusCode.Created);
        await (await OwnerAsync(host)).AssignAsync(away, Today(), job, [elsewhere]).BodyAsync(HttpStatusCode.Created);
        host.Attendance.CheckIn(first.Id, Today(), DateTimeOffset.UtcNow.AddHours(-1));

        var day = await manager.GetFromJsonAsync<JsonElement>($"/api/work-days/{Iso(Today())}");

        Assert.Equal(new[] { raft.Id, empty.Id }.Order(), day.GetProperty("rafts").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).Order());
        Assert.DoesNotContain(day.GetProperty("rafts").EnumerateArray(), r => r.GetProperty("id").GetGuid() == discontinued.Id || r.GetProperty("id").GetGuid() == away.Id);
        Assert.True(day.GetProperty("canAssign").GetBoolean());
        var crew = RaftOf(day, raft).GetProperty("crew").EnumerateArray().ToList();
        Assert.Equal([first.Id, second.Id], crew.Select(m => m.GetProperty("employeeId").GetGuid()));
        Assert.Equal((job.Name, false, true), (crew[0].GetProperty("jobTypeName").GetString(), crew[0].GetProperty("canEdit").GetBoolean(), crew[1].GetProperty("canEdit").GetBoolean()));
        Assert.NotEqual(JsonValueKind.Null, crew[0].GetProperty("checkInAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, crew[1].GetProperty("checkInAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, crew[0].GetProperty("hours").ValueKind);
        Assert.True(RaftOf(day, raft).GetProperty("canChangeJob").GetBoolean());
        Assert.False(RaftOf(day, empty).GetProperty("canChangeJob").GetBoolean());
        Assert.Empty(RaftOf(day, empty).GetProperty("crew").EnumerateArray());
    }

    [Fact]
    public async Task The_owner_sees_the_rafts_of_every_zone()
    {
        var (zoneA, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var (raftA, raftB) = (await AddRaftAsync(host, zoneA), await AddRaftAsync(host, zoneB));

        var day = await (await OwnerAsync(host)).GetFromJsonAsync<JsonElement>($"/api/work-days/{Iso(Today())}");

        Assert.Equal(JsonValueKind.Object, RaftOf(day, raftA).ValueKind);
        Assert.Equal(zoneB.Id, RaftOf(day, raftB).GetProperty("zoneId").GetGuid());
    }

    [Fact]
    public async Task A_past_date_is_shown_but_cannot_be_assigned_or_edited()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        await StoreAssignmentAsync(host, employee, raft, job, Today().AddDays(-2));

        var past = await manager.GetFromJsonAsync<JsonElement>($"/api/work-days/{Iso(Today().AddDays(-2))}");
        var future = await manager.GetFromJsonAsync<JsonElement>($"/api/work-days/{Iso(Today().AddDays(2))}");

        var member = Assert.Single(RaftOf(past, raft).GetProperty("crew").EnumerateArray());
        Assert.Equal((false, false), (past.GetProperty("canAssign").GetBoolean(), member.GetProperty("canEdit").GetBoolean()));
        Assert.True(future.GetProperty("canAssign").GetBoolean());
        Assert.Empty(RaftOf(future, raft).GetProperty("crew").EnumerateArray());
    }

    [Fact]
    public async Task The_staff_list_holds_the_staff_employees_of_the_callers_zones_only()
    {
        var (zone, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (staff, away, worker) = (await AddEmployeeAsync(host, [zone], EmployeeType.Staff), await AddEmployeeAsync(host, [zoneB], EmployeeType.Staff), await AddEmployeeAsync(host, [zone]));
        host.Attendance.CheckIn(staff.Id, Today(), DateTimeOffset.UtcNow.AddHours(-1));

        var day = await manager.GetFromJsonAsync<JsonElement>($"/api/work-days/{Iso(Today())}");

        var listed = Assert.Single(day.GetProperty("staff").EnumerateArray());
        Assert.Equal(staff.Id, listed.GetProperty("employeeId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("checkInAt").ValueKind);
        Assert.DoesNotContain(day.GetProperty("staff").EnumerateArray(), s => s.GetProperty("employeeId").GetGuid() == away.Id || s.GetProperty("employeeId").GetGuid() == worker.Id);
    }

    // AC-10 on the screen: 06:10 / 09:40 / 16:55 with the break 11:30-12:30 and a unit of 15 minutes is 3 h 30 and 6 h 15.
    [Fact]
    public async Task A_complete_day_shows_the_rounded_hours_of_each_job_type()
    {
        await using var own = new WorkAssignmentHost();
        var zone = await own.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(own, StaffRole.ZoneManager, zone);
        var (raft, harvesting, hanging, employee) = (await AddRaftAsync(own, zone), await AddJobTypeAsync(own), await AddJobTypeAsync(own), await AddEmployeeAsync(own, [zone]));
        var (date, vietnam) = (new DateOnly(2026, 3, 10), TimeSpan.FromHours(7));
        DateTimeOffset At(int hour, int minute) => new(date.Year, date.Month, date.Day, hour, minute, 0, vietnam);
        var accountId = await own.QueryAsync(db => db.Accounts.Select(a => a.Id).FirstAsync());
        await own.UseDbAsync(db => db.AttendanceSettingsVersions.Add(new AttendanceSettingsVersion
        {
            EffectiveFrom = new DateOnly(2020, 1, 1), BreakStart = new TimeOnly(11, 30), BreakEnd = new TimeOnly(12, 30), RoundingUnit = AttendanceRoundingUnit.Minutes15, EnteredByAccountId = accountId,
        }));
        await StoreAssignmentAsync(own, employee, raft, harvesting, date);
        await StoreAssignmentAsync(own, employee, raft, hanging, date, At(9, 40));
        own.Attendance.CheckIn(employee.Id, date, At(6, 10), At(16, 55));

        var day = await manager.GetFromJsonAsync<JsonElement>($"/api/work-days/{Iso(date)}");

        var member = Assert.Single(RaftOf(day, raft).GetProperty("crew").EnumerateArray());
        var hours = member.GetProperty("hours").EnumerateArray().Select(h => (h.GetProperty("jobTypeName").GetString(), h.GetProperty("minutes").GetInt32())).ToList();
        Assert.Equal([(harvesting.Name, 210), (hanging.Name, 375)], hours);
        Assert.Equal(hanging.Name, member.GetProperty("jobTypeName").GetString());
        Assert.Equal(2, member.GetProperty("periods").GetArrayLength());
    }

    [Fact]
    public async Task The_accountant_and_a_caller_who_is_not_logged_in_are_refused()
    {
        var accountant = await StaffInZoneAsync(host, StaffRole.Accountant, null);

        await (await accountant.GetAsync($"/api/work-days/{Iso(Today())}")).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync($"/api/work-days/{Iso(Today())}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await (await OwnerAsync(host)).GetAsync("/api/work-days/not-a-date")).StatusCode);
    }
}
