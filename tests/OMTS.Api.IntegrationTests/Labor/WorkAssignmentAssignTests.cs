using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-02.0: assign a job type and a raft to employees for a date; the checks of BR-LABOR-03 and the messages that answer them.</summary>
public class WorkAssignmentAssignTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    [Fact]
    public async Task A_zone_manager_assigns_employees_to_a_raft_of_their_zone_and_the_zone_joins_their_working_zones()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var other = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host));
        var inZone = await AddEmployeeAsync(host, [zone]);
        var elsewhere = await AddEmployeeAsync(host, [other]);
        var (created, zoneAdded) = (await host.AuditCountAsync("WorkAssignment.Created"), await host.AuditCountAsync("Employee.WorkingZoneAdded"));

        var response = await manager.AssignAsync(raft, Today(), job, [inZone, elsewhere]);

        var saved = await response.BodyAsync(HttpStatusCode.Created);
        Assert.Equal("MSG-GEN-05", saved.GetProperty("messageCode").GetString());
        Assert.Equal(2, saved.GetProperty("assignments").GetArrayLength());
        var first = saved.First();
        Assert.Equal(inZone.Id, first.GetProperty("employeeId").GetGuid());
        Assert.Equal((raft.Code, job.Name, Iso(Today()), zone.Id), (first.GetProperty("raftCode").GetString(), first.GetProperty("jobTypeName").GetString(), first.GetProperty("date").GetString(), first.GetProperty("zoneId").GetGuid()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, first.GetProperty("startTime").ValueKind);
        var row = Assert.Single(await host.RowsOfAsync(elsewhere, Today()));
        Assert.Equal((raft.Id, job.Id, (DateTimeOffset?)null), (row.RaftId, row.JobTypeId, row.StartTime));
        var zones = await host.QueryAsync(db => db.EmployeeWorkingZones.Where(w => w.EmployeeId == elsewhere.Id).Select(w => w.ZoneId).ToListAsync());
        Assert.Equal(new[] { other.Id, zone.Id }.Order(), zones.Order());
        Assert.Equal(created + 2, await host.AuditCountAsync("WorkAssignment.Created"));
        Assert.Equal(zoneAdded + 1, await host.AuditCountAsync("Employee.WorkingZoneAdded"));
    }

    [Fact]
    public async Task The_employees_are_pushed_with_msg_labor_05_after_the_save()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));

        await manager.AssignAsync(raft, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);

        var notice = Assert.Single(host.Pushes.Notices, n => n.EmployeeId == employee.Id);
        Assert.Equal("MSG-LABOR-05", notice.Message.Code);
        Assert.Equal(Today(), notice.Date);
        Assert.Contains($"{job.Name} on raft {raft.Code}", notice.Message.Text);
    }

    [Fact]
    public async Task A_push_that_fails_leaves_the_assignment_saved()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        host.Pushes.Fails = true;
        try
        {
            await manager.AssignAsync(raft, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);
        }
        finally
        {
            host.Pushes.Fails = false;
        }

        Assert.Single(await host.RowsOfAsync(employee, Today()));
    }

    [Fact]
    public async Task The_owner_assigns_in_any_zone()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var owner = await OwnerAsync(host);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, []));

        await owner.AssignAsync(raft, Today().AddDays(3), job, [employee]).BodyAsync(HttpStatusCode.Created);

        Assert.Single(await host.RowsOfAsync(employee, Today().AddDays(3)));
    }

    [Fact]
    public async Task Work_is_assigned_for_today_or_later_only()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));

        var yesterday = await manager.AssignAsync(raft, Today().AddDays(-1), job, [employee]);
        var today = await manager.AssignAsync(raft, Today(), job, [employee]);

        var problem = await yesterday.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        Assert.Equal("Date", problem.Str("field"));
        Assert.Equal(HttpStatusCode.Created, today.StatusCode);
        Assert.Empty(await host.RowsOfAsync(employee, Today().AddDays(-1)));
    }

    [Fact]
    public async Task A_missing_date_is_msg_gen_01_on_Date()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));

        var response = await manager.PostAsJsonAsync($"/api/rafts/{raft.Id}/work-assignments", new { jobTypeId = job.Id, employeeIds = new[] { employee.Id } });

        Assert.Equal("Date", (await response.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("empty-id")]
    [InlineData("too-many")]
    public async Task The_employees_must_be_a_list_of_known_employees_of_at_most_200(string case_)
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        object? ids = case_ switch
        {
            "none" => null,
            "empty" => Array.Empty<Guid>(),
            "unknown" => new[] { employee.Id, Guid.NewGuid() },
            "empty-id" => new[] { Guid.Empty },
            _ => Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray(),
        };

        var response = await manager.PostAsJsonAsync($"/api/rafts/{raft.Id}/work-assignments", new { date = Iso(Today()), jobTypeId = job.Id, employeeIds = ids });

        Assert.Equal("Employees", (await response.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        Assert.Empty(await host.RowsOfAsync(employee, Today()));
    }

    [Fact]
    public async Task The_job_type_must_exist_and_be_in_use()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, employee) = (await AddRaftAsync(host, zone), await AddEmployeeAsync(host, [zone]));
        var discontinued = await AddJobTypeAsync(host, StatusIds.Usage.Discontinued);

        var unknown = await manager.PostAsJsonAsync($"/api/rafts/{raft.Id}/work-assignments", new { date = Iso(Today()), jobTypeId = Guid.NewGuid(), employeeIds = new[] { employee.Id } });
        var gone = await manager.AssignAsync(raft, Today(), discontinued, [employee]);

        Assert.Equal("Job type", (await unknown.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        Assert.Equal("jobTypeId", (await gone.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-09", "MSG01")).Str("field"));
        Assert.Empty(await host.RowsOfAsync(employee, Today()));
    }

    [Fact]
    public async Task A_raft_not_in_use_is_msg_gen_09_and_an_unknown_raft_is_404()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (job, employee) = (await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        var discontinued = await AddRaftAsync(host, zone, StatusIds.Usage.Discontinued);

        Assert.Equal("MSG-GEN-09", await (await manager.AssignAsync(discontinued, Today(), job, [employee])).ErrorCodeAsync());
        var unknown = await manager.PostAsJsonAsync($"/api/rafts/{Guid.NewGuid()}/work-assignments", new { date = Iso(Today()), jobTypeId = job.Id, employeeIds = new[] { employee.Id } });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(await host.RowsOfAsync(employee, Today()));
    }

    [Fact]
    public async Task An_employee_who_is_left_cannot_be_assigned_and_nobody_in_the_request_is_saved()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host));
        var (employed, left) = (await AddEmployeeAsync(host, [zone]), await AddEmployeeAsync(host, [zone], change: e => e.StatusId = StatusIds.Employee.Left));

        var response = await manager.AssignAsync(raft, Today(), job, [employed, left]);

        Assert.Equal("employeeIds", (await response.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-09", "MSG01")).Str("field"));
        Assert.Empty(await host.RowsOfAsync(employed, Today()));
        Assert.Empty(await host.RowsOfAsync(left, Today()));
    }

    // BR-LABOR-01: the assignments after the last working day are removed, so none is made for a day after it.
    [Fact]
    public async Task A_date_after_the_last_working_day_of_the_employee_is_refused()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host));
        var ending = await AddEmployeeAsync(host, [zone], change: e => e.LastWorkingDay = Today());

        var later = await manager.AssignAsync(raft, Today().AddDays(1), job, [ending]);
        var lastDay = await manager.AssignAsync(raft, Today(), job, [ending]);

        Assert.Equal("Date", (await later.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        Assert.Equal(HttpStatusCode.Created, lastDay.StatusCode);
    }

    [Fact]
    public async Task One_employee_named_twice_is_assigned_once()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));

        var saved = await manager.AssignAsync(raft, Today(), job, [employee, employee]).BodyAsync(HttpStatusCode.Created);

        Assert.Equal(1, saved.GetProperty("assignments").GetArrayLength());
        Assert.Single(await host.RowsOfAsync(employee, Today()));
    }

    [Fact]
    public async Task Assigning_again_the_same_work_changes_nothing_and_pushes_nothing()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        await manager.AssignAsync(raft, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);
        var audits = await host.AuditCountAsync("WorkAssignment.Created");

        var again = await manager.AssignAsync(raft, Today(), job, [employee]).BodyAsync(HttpStatusCode.Created);

        Assert.Equal(1, again.GetProperty("assignments").GetArrayLength());
        Assert.Single(await host.RowsOfAsync(employee, Today()));
        Assert.Equal(audits, await host.AuditCountAsync("WorkAssignment.Created"));
        Assert.Single(host.Pushes.Notices, n => n.EmployeeId == employee.Id);
    }

    [Fact]
    public async Task The_same_raft_with_another_job_type_changes_the_job_without_a_question()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, harvesting, hanging, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        await manager.AssignAsync(raft, Today(), harvesting, [employee]).BodyAsync(HttpStatusCode.Created);
        var (updated, moved) = (await host.AuditCountAsync("WorkAssignment.Updated"), await host.AuditCountAsync("WorkAssignment.Moved"));

        await manager.AssignAsync(raft, Today(), hanging, [employee]).BodyAsync(HttpStatusCode.Created);

        Assert.Equal(hanging.Id, Assert.Single(await host.RowsOfAsync(employee, Today())).JobTypeId);
        Assert.Equal((updated + 1, moved), (await host.AuditCountAsync("WorkAssignment.Updated"), await host.AuditCountAsync("WorkAssignment.Moved")));
    }

    // BR-LABOR-13: nothing dated on or before the last day of a payroll Awaiting finalization or Finalized, or of a later one, changes.
    [Fact]
    public async Task A_date_held_by_a_locked_pay_period_is_msg_labor_04_and_the_day_after_it_is_saved()
    {
        await using var own = new WorkAssignmentHost();
        var zone = await own.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(own, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(own, zone), await AddJobTypeAsync(own), await AddEmployeeAsync(own, [zone]));
        var start = Today().AddDays(30);
        var end = start.AddDays(29);
        await own.AddAwaitingPayrollAsync(Iso(start), Iso(end));

        var inside = await manager.AssignAsync(raft, start.AddDays(5), job, [employee]);
        var before = await manager.AssignAsync(raft, Today(), job, [employee]);
        var after = await manager.AssignAsync(raft, end.AddDays(1), job, [employee]);

        var problem = await inside.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Equal(Iso(end), problem.Str("lockedUntil"));
        await before.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Equal(HttpStatusCode.Created, after.StatusCode);
        Assert.Empty(await own.RowsOfAsync(employee, start.AddDays(5)));
    }

    [Fact]
    public async Task A_payroll_not_sent_does_not_lock_and_the_save_touches_it()
    {
        await using var own = new WorkAssignmentHost();
        var zone = await own.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(own, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(own, zone), await AddJobTypeAsync(own), await AddEmployeeAsync(own, [zone]));
        var start = Today().AddDays(30);
        var payroll = await own.AddPayrollAsync(Iso(start), Iso(start.AddDays(29)), StatusIds.Payroll.Draft);

        await manager.AssignAsync(raft, start.AddDays(5), job, [employee]).BodyAsync(HttpStatusCode.Created);

        // The row is written by the same save, so a parallel send of the payroll meets a new row version.
        Assert.NotNull(await own.QueryAsync(db => db.Payrolls.Where(p => p.Id == payroll.Id).Select(p => p.UpdatedAt).SingleAsync()));
    }
}
