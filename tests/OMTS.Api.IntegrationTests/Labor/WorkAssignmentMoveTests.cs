using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-02.4 and LABOR-02.0.E2: an employee already assigned that day is moved only after a question, and one checked in only by Change job for crew.</summary>
public class WorkAssignmentMoveTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    [Fact]
    public async Task An_employee_assigned_to_another_raft_is_asked_about_with_msg_labor_06_and_nothing_is_saved()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, target, job, employee) = (await AddRaftAsync(host, zone), await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        await manager.AssignAsync(raft, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);

        var response = await manager.AssignAsync(target, Today(), job, [employee]);

        var problem = await response.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-06", null);
        Assert.Equal($"{employee.FullName} is already assigned to raft {raft.Code} on {Today():dd/MM/yyyy}. Move {employee.FullName} to raft {target.Code}?", problem.Detail());
        Assert.Equal("moveConfirmed", problem.Str("confirmationField"));
        var move = Assert.Single(problem.GetProperty("moves").EnumerateArray());
        Assert.Equal((employee.Id, raft.Code, target.Code), (move.GetProperty("employeeId").GetGuid(), move.GetProperty("raftCode").GetString(), move.GetProperty("newRaftCode").GetString()));
        Assert.Equal(raft.Id, Assert.Single(await host.RowsOfAsync(employee, Today())).RaftId);
    }

    [Fact]
    public async Task A_confirmed_move_replaces_the_assignment_and_tells_the_employee()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, target, job, employee) = (await AddRaftAsync(host, zone), await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        var created = (await manager.AssignAsync(raft, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created)).First();
        var moved = await host.AuditCountAsync("WorkAssignment.Moved");

        var response = await manager.AssignAsync(target, Today(), job, [employee], moveConfirmed: true).BodyAsync(HttpStatusCode.Created);

        var row = Assert.Single(await host.RowsOfAsync(employee, Today()));
        Assert.Equal((created.GetProperty("id").GetGuid(), target.Id), (row.Id, row.RaftId));
        Assert.Equal(row.Id, response.First().GetProperty("id").GetGuid());
        Assert.Equal(moved + 1, await host.AuditCountAsync("WorkAssignment.Moved"));
        Assert.Equal(2, host.Pushes.Notices.Count(n => n.EmployeeId == employee.Id));
        Assert.Contains($"on raft {target.Code}", host.Pushes.Notices.Last(n => n.EmployeeId == employee.Id).Message.Text);
    }

    [Fact]
    public async Task The_question_lists_every_employee_to_move_and_one_confirmation_moves_them_all()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, target, job) = (await AddRaftAsync(host, zone), await AddRaftAsync(host, zone), await AddJobTypeAsync(host));
        var (one, two, fresh) = (await AddEmployeeAsync(host, [zone]), await AddEmployeeAsync(host, [zone]), await AddEmployeeAsync(host, [zone]));
        await manager.AssignAsync(raft, Today(), job, [one, two]).BodyAsync(HttpStatusCode.Created);

        var asked = await manager.AssignAsync(target, Today(), job, [one, fresh, two]);
        await manager.AssignAsync(target, Today(), job, [one, fresh, two], moveConfirmed: true).BodyAsync(HttpStatusCode.Created);

        var problem = await asked.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-06", null);
        Assert.Equal([one.Id, two.Id], problem.GetProperty("moves").EnumerateArray().Select(m => m.GetProperty("employeeId").GetGuid()));
        foreach (var employee in new[] { one, two, fresh })
        {
            Assert.Equal(target.Id, Assert.Single(await host.RowsOfAsync(employee, Today())).RaftId);
        }
    }

    [Fact]
    public async Task An_employee_assigned_on_another_date_is_not_a_move()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, target, job, employee) = (await AddRaftAsync(host, zone), await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        await manager.AssignAsync(raft, Today().AddDays(1), job, [employee]).BodyAsync(HttpStatusCode.Created);

        await manager.AssignAsync(target, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);

        Assert.Single(await host.RowsOfAsync(employee, Today()));
        Assert.Single(await host.RowsOfAsync(employee, Today().AddDays(1)));
    }

    [Fact]
    public async Task An_employee_already_checked_in_is_msg_labor_07_and_nobody_in_the_request_is_saved()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, target, job) = (await AddRaftAsync(host, zone), await AddRaftAsync(host, zone), await AddJobTypeAsync(host));
        var (checkedIn, fresh) = (await AddEmployeeAsync(host, [zone]), await AddEmployeeAsync(host, [zone]));
        await manager.AssignAsync(raft, Today(), job, [checkedIn]).BodyAsync(HttpStatusCode.Created);
        host.Attendance.CheckIn(checkedIn.Id, Today(), DateTimeOffset.UtcNow.AddHours(-1));

        var response = await manager.AssignAsync(target, Today(), job, [fresh, checkedIn], moveConfirmed: true);

        var problem = await response.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-07", null);
        Assert.Equal($"{checkedIn.FullName} is already checked in today. To move {checkedIn.FullName} to another raft, use Change job for crew.", problem.Detail());
        Assert.Equal([checkedIn.Id], problem.GetProperty("employeeIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Empty(await host.RowsOfAsync(fresh, Today()));
        Assert.Equal(raft.Id, Assert.Single(await host.RowsOfAsync(checkedIn, Today())).RaftId);
    }

    [Fact]
    public async Task An_employee_checked_in_without_any_assignment_is_msg_labor_07_too()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        host.Attendance.CheckIn(employee.Id, Today(), DateTimeOffset.UtcNow.AddHours(-1));

        await (await manager.AssignAsync(raft, Today(), job, [employee])).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-07", null);

        Assert.Empty(await host.RowsOfAsync(employee, Today()));
    }

    // The attendance of another day does not stop an assignment for this one.
    [Fact]
    public async Task A_check_in_on_another_date_is_no_obstacle()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        host.Attendance.CheckIn(employee.Id, Today(), DateTimeOffset.UtcNow.AddHours(-1));

        await manager.AssignAsync(raft, Today().AddDays(1), job, [employee]).BodyAsync(HttpStatusCode.Created);
    }

    // BR-LABOR-03: the move takes an employee off the crew of the old raft, which is the call of the Zone Manager of its zone.
    [Fact]
    public async Task A_zone_manager_cannot_move_an_employee_off_a_raft_of_a_zone_they_do_not_work_in()
    {
        var (zoneA, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var (managerA, managerB) = (await StaffInZoneAsync(host, StaffRole.ZoneManager, zoneA), await StaffInZoneAsync(host, StaffRole.ZoneManager, zoneB));
        var (raftA, raftB, job, employee) = (await AddRaftAsync(host, zoneA), await AddRaftAsync(host, zoneB), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zoneA, zoneB]));
        await managerB.AssignAsync(raftB, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);

        var response = await managerA.AssignAsync(raftA, Today(), job, [employee], moveConfirmed: true);

        await response.SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        Assert.Equal(raftB.Id, Assert.Single(await host.RowsOfAsync(employee, Today())).RaftId);
    }

    [Fact]
    public async Task The_owner_moves_an_employee_between_zones()
    {
        var (zoneA, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var (owner, managerB) = (await OwnerAsync(host), await StaffInZoneAsync(host, StaffRole.ZoneManager, zoneB));
        var (raftA, raftB, job, employee) = (await AddRaftAsync(host, zoneA), await AddRaftAsync(host, zoneB), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zoneB]));
        await managerB.AssignAsync(raftB, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);

        await owner.AssignAsync(raftA, Today(), job, [employee], moveConfirmed: true).BodyAsync(HttpStatusCode.Created);

        Assert.Equal(raftA.Id, Assert.Single(await host.RowsOfAsync(employee, Today())).RaftId);
        Assert.Contains(zoneA.Id, await host.QueryAsync(db => db.EmployeeWorkingZones.Where(w => w.EmployeeId == employee.Id).Select(w => w.ZoneId).ToListAsync()));
    }
}
