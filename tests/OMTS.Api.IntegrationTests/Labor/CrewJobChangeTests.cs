using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-02.1 Change job for crew: a new job period from now for the members at work, the assignment changed for those not checked in, and no push.</summary>
public class CrewJobChangeTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    private sealed record Crew(
        HttpClient Manager, FarmingZone Zone, Raft Raft, Raft Other, JobType Harvesting, JobType Hanging, Employee AtWork, Employee NotYet, Employee Finished);

    private async Task<Crew> CrewAsync()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, other, harvesting, hanging) = (await AddRaftAsync(host, zone), await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddJobTypeAsync(host));
        var (atWork, notYet, finished) = (await AddEmployeeAsync(host, [zone]), await AddEmployeeAsync(host, [zone]), await AddEmployeeAsync(host, [zone]));
        await manager.AssignAsync(raft, Today(), harvesting, [atWork, notYet, finished]).BodyAsync(HttpStatusCode.Created);
        host.Attendance.CheckIn(atWork.Id, Today(), DateTimeOffset.UtcNow.AddHours(-2));
        host.Attendance.CheckIn(finished.Id, Today(), DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-1));
        return new Crew(manager, zone, raft, other, harvesting, hanging, atWork, notYet, finished);
    }

    private static string ResultOf(JsonElement changed, Employee employee) =>
        changed.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("employeeId").GetGuid() == employee.Id).GetProperty("result").GetString()!;

    [Fact]
    public async Task A_member_at_work_gets_a_new_job_period_from_now_and_one_not_checked_in_has_the_assignment_changed()
    {
        var c = await CrewAsync();
        var before = DateTimeOffset.UtcNow;
        var pushes = host.Pushes.Notices.Count;
        var (periodsAdded, updated) = (await host.AuditCountAsync("WorkAssignment.JobPeriodAdded"), await host.AuditCountAsync("WorkAssignment.Updated"));

        var changed = await c.Manager.ChangeJobAsync(c.Raft, c.Hanging, [c.AtWork, c.NotYet]).BodyAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-GEN-05", changed.GetProperty("messageCode").GetString());
        Assert.Equal(("JobPeriodAdded", "AssignmentChanged"), (ResultOf(changed, c.AtWork), ResultOf(changed, c.NotYet)));
        var periods = await host.RowsOfAsync(c.AtWork, Today());
        Assert.Equal(2, periods.Length);
        Assert.Equal((c.Harvesting.Id, (DateTimeOffset?)null), (periods[0].JobTypeId, periods[0].StartTime));
        Assert.Equal((c.Hanging.Id, c.Raft.Id), (periods[1].JobTypeId, periods[1].RaftId));
        Assert.InRange(periods[1].StartTime!.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        var waiting = Assert.Single(await host.RowsOfAsync(c.NotYet, Today()));
        Assert.Equal((c.Hanging.Id, (DateTimeOffset?)null), (waiting.JobTypeId, waiting.StartTime));
        Assert.Equal(c.Harvesting.Id, Assert.Single(await host.RowsOfAsync(c.Finished, Today())).JobTypeId);
        Assert.Equal((periodsAdded + 1, updated + 1), (await host.AuditCountAsync("WorkAssignment.JobPeriodAdded"), await host.AuditCountAsync("WorkAssignment.Updated")));
        // BR-LABOR-04: the crew is told on site.
        Assert.Equal(pushes, host.Pushes.Notices.Count);
    }

    [Fact]
    public async Task A_crew_that_moves_goes_to_the_new_raft_and_is_no_longer_the_crew_of_the_old_one()
    {
        var c = await CrewAsync();

        await c.Manager.ChangeJobAsync(c.Raft, c.Harvesting, [c.AtWork, c.NotYet], newRaft: c.Other).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(c.Other.Id, (await host.RowsOfAsync(c.AtWork, Today()))[^1].RaftId);
        Assert.Equal(c.Other.Id, Assert.Single(await host.RowsOfAsync(c.NotYet, Today())).RaftId);
        var again = await c.Manager.ChangeJobAsync(c.Raft, c.Hanging, [c.AtWork]);
        Assert.Equal("Employees", (await again.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
    }

    [Fact]
    public async Task Each_change_adds_one_more_period_and_the_latest_is_the_one_in_force()
    {
        var c = await CrewAsync();
        await c.Manager.ChangeJobAsync(c.Raft, c.Hanging, [c.AtWork]).BodyAsync(HttpStatusCode.OK);
        await Task.Delay(20);

        await c.Manager.ChangeJobAsync(c.Raft, c.Harvesting, [c.AtWork]).BodyAsync(HttpStatusCode.OK);

        var periods = await host.RowsOfAsync(c.AtWork, Today());
        Assert.Equal([c.Harvesting.Id, c.Hanging.Id, c.Harvesting.Id], periods.Select(p => p.JobTypeId));
    }

    [Fact]
    public async Task Members_who_keep_their_job_are_left_as_they_are()
    {
        var c = await CrewAsync();

        var changed = await c.Manager.ChangeJobAsync(c.Raft, c.Harvesting, [c.AtWork, c.NotYet]);

        // Every selected member keeps the job, so the answer is a success with nothing added.
        var body = await changed.BodyAsync(HttpStatusCode.OK);
        Assert.Equal(("Unchanged", "Unchanged"), (ResultOf(body, c.AtWork), ResultOf(body, c.NotYet)));
        Assert.Single(await host.RowsOfAsync(c.AtWork, Today()));
    }

    [Fact]
    public async Task With_no_selected_member_at_work_the_answer_is_msg_gen_09_and_nothing_changes()
    {
        var c = await CrewAsync();

        var response = await c.Manager.ChangeJobAsync(c.Raft, c.Hanging, [c.NotYet]);

        Assert.Equal("MSG-GEN-09", await response.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(c.Harvesting.Id, Assert.Single(await host.RowsOfAsync(c.NotYet, Today())).JobTypeId);
    }

    [Fact]
    public async Task A_member_who_checked_out_or_is_not_on_the_crew_is_refused_and_nobody_changes()
    {
        var c = await CrewAsync();
        var stranger = await AddEmployeeAsync(host, [c.Zone]);
        await c.Manager.AssignAsync(c.Other, Today(), c.Harvesting, [stranger]).BodyAsync(HttpStatusCode.Created);
        var unassigned = await AddEmployeeAsync(host, [c.Zone]);

        foreach (var member in new[] { c.Finished, stranger, unassigned })
        {
            var response = await c.Manager.ChangeJobAsync(c.Raft, c.Hanging, [c.AtWork, member]);
            Assert.Equal("Employees", (await response.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        }

        Assert.Single(await host.RowsOfAsync(c.AtWork, Today()));
    }

    [Fact]
    public async Task The_job_type_the_employees_and_the_rafts_must_be_usable()
    {
        var c = await CrewAsync();
        var (discontinued, gone) = (await AddJobTypeAsync(host, StatusIds.Usage.Discontinued), await AddRaftAsync(host, c.Zone, StatusIds.Usage.Discontinued));

        Assert.Equal("MSG-GEN-09", await (await c.Manager.ChangeJobAsync(c.Raft, discontinued, [c.AtWork])).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-09", await (await c.Manager.ChangeJobAsync(c.Raft, c.Hanging, [c.AtWork], newRaft: gone)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-09", await (await c.Manager.ChangeJobAsync(gone, c.Hanging, [c.AtWork])).ErrorCodeAsync());
        Assert.Equal("Employees", (await (await c.Manager.ChangeJobAsync(c.Raft, c.Hanging, [])).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.Manager.ChangeJobAsync(new Raft(), c.Hanging, [c.AtWork])).StatusCode);
        Assert.Single(await host.RowsOfAsync(c.AtWork, Today()));
    }

    [Fact]
    public async Task Another_zones_raft_is_msg_gen_08_for_the_route_and_for_the_new_raft()
    {
        var c = await CrewAsync();
        var (zoneB, owner) = (await host.AddZoneAsync(NewCode()), await OwnerAsync(host));
        var (managerB, away) = (await StaffInZoneAsync(host, StaffRole.ZoneManager, zoneB), await AddRaftAsync(host, zoneB));

        await (await managerB.ChangeJobAsync(c.Raft, c.Hanging, [c.AtWork])).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        await (await c.Manager.ChangeJobAsync(c.Raft, c.Hanging, [c.AtWork], newRaft: away)).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");

        Assert.Single(await host.RowsOfAsync(c.AtWork, Today()));
        await owner.ChangeJobAsync(c.Raft, c.Hanging, [c.AtWork], newRaft: away).BodyAsync(HttpStatusCode.OK);
        Assert.Equal(away.Id, (await host.RowsOfAsync(c.AtWork, Today()))[^1].RaftId);
    }

    [Fact]
    public async Task A_pay_period_that_holds_today_refuses_the_change_with_msg_labor_04()
    {
        await using var own = new WorkAssignmentHost();
        var zone = await own.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(own, StaffRole.ZoneManager, zone);
        var (raft, job, other, employee) = (await AddRaftAsync(own, zone), await AddJobTypeAsync(own), await AddJobTypeAsync(own), await AddEmployeeAsync(own, [zone]));
        await manager.AssignAsync(raft, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);
        own.Attendance.CheckIn(employee.Id, Today(), DateTimeOffset.UtcNow.AddHours(-1));
        await own.AddAwaitingPayrollAsync(Iso(Today().AddDays(-3)), Iso(Today().AddDays(10)));

        var response = await manager.ChangeJobAsync(raft, other, [employee]);

        await response.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Single(await own.RowsOfAsync(employee, Today()));
    }
}
