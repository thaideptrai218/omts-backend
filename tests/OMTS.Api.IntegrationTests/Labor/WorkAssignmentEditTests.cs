using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
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

/// <summary>LABOR-02.2: edit or remove the work assigned to one employee, until the employee is checked in, for today or later.</summary>
public class WorkAssignmentEditTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    private sealed record Setup(HttpClient Manager, FarmingZone Zone, Raft Raft, JobType Job, Employee Employee, string Id, string RowVersion);

    private async Task<Setup> AssignedAsync(DateOnly? date = null, Action<Employee>? change = null)
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone], change: change));
        var created = (await manager.AssignAsync(raft, date ?? Today(), job, [employee]).BodyAsync(HttpStatusCode.Created)).First();
        var id = created.GetProperty("id").GetGuid();
        return new Setup(manager, zone, raft, job, employee, id.ToString(), await host.IssueVersionAsync(id));
    }

    private static Task<HttpResponseMessage> EditAsync(Setup s, object body, string? id = null) => s.Manager.PutAsJsonAsync($"/api/work-assignments/{id ?? s.Id}", body);

    [Fact]
    public async Task A_new_raft_moves_the_assignment_and_the_employee_is_pushed_the_change()
    {
        var s = await AssignedAsync();
        var target = await AddRaftAsync(host, s.Zone);
        var moved = await host.AuditCountAsync("WorkAssignment.Moved");

        var saved = await (await EditAsync(s, new { raftId = target.Id, rowVersion = s.RowVersion })).BodyAsync(HttpStatusCode.OK);

        Assert.Equal((target.Code, s.Job.Name), (saved.GetProperty("assignment").GetProperty("raftCode").GetString(), saved.GetProperty("assignment").GetProperty("jobTypeName").GetString()));
        Assert.Equal("MSG-GEN-05", saved.GetProperty("messageCode").GetString());
        var row = Assert.Single(await host.RowsOfAsync(s.Employee, Today()));
        Assert.Equal((target.Id, s.Job.Id), (row.RaftId, row.JobTypeId));
        Assert.Equal(moved + 1, await host.AuditCountAsync("WorkAssignment.Moved"));
        Assert.Contains($"on raft {target.Code}", host.Pushes.Notices.Last(n => n.EmployeeId == s.Employee.Id).Message.Text);
    }

    [Fact]
    public async Task A_new_job_type_changes_only_the_job()
    {
        var s = await AssignedAsync();
        var other = await AddJobTypeAsync(host);
        var updated = await host.AuditCountAsync("WorkAssignment.Updated");

        await (await EditAsync(s, new { jobTypeId = other.Id, rowVersion = s.RowVersion })).BodyAsync(HttpStatusCode.OK);

        var row = Assert.Single(await host.RowsOfAsync(s.Employee, Today()));
        Assert.Equal((s.Raft.Id, other.Id), (row.RaftId, row.JobTypeId));
        Assert.Equal(updated + 1, await host.AuditCountAsync("WorkAssignment.Updated"));
    }

    [Fact]
    public async Task The_same_values_change_nothing_and_push_nothing()
    {
        var s = await AssignedAsync();
        var pushes = host.Pushes.Notices.Count(n => n.EmployeeId == s.Employee.Id);

        await (await EditAsync(s, new { raftId = s.Raft.Id, jobTypeId = s.Job.Id, rowVersion = s.RowVersion })).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(pushes, host.Pushes.Notices.Count(n => n.EmployeeId == s.Employee.Id));
    }

    [Fact]
    public async Task An_edit_names_a_raft_or_a_job_type_and_the_version_it_read()
    {
        var s = await AssignedAsync();

        var nothing = await EditAsync(s, new { rowVersion = s.RowVersion });
        var noVersion = await EditAsync(s, new { jobTypeId = s.Job.Id });
        var stale = await EditAsync(s, new { jobTypeId = s.Job.Id, rowVersion = StaleRowVersion });

        Assert.Equal("Raft or job type", (await nothing.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        Assert.Equal("RowVersion", (await noVersion.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task The_new_raft_and_job_type_must_exist_and_be_in_use_and_the_raft_in_the_callers_zones()
    {
        var s = await AssignedAsync();
        var (gone, discontinued, away) = (await AddRaftAsync(host, s.Zone, StatusIds.Usage.Discontinued), await AddJobTypeAsync(host, StatusIds.Usage.Discontinued), await AddRaftAsync(host, await host.AddZoneAsync(NewCode())));

        Assert.Equal("MSG-GEN-09", await (await EditAsync(s, new { raftId = gone.Id, rowVersion = s.RowVersion })).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-09", await (await EditAsync(s, new { jobTypeId = discontinued.Id, rowVersion = s.RowVersion })).ErrorCodeAsync());
        Assert.Equal("Raft", (await (await EditAsync(s, new { raftId = Guid.NewGuid(), rowVersion = s.RowVersion })).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        await (await EditAsync(s, new { raftId = away.Id, rowVersion = s.RowVersion })).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        Assert.Equal(s.Raft.Id, Assert.Single(await host.RowsOfAsync(s.Employee, Today())).RaftId);
    }

    [Fact]
    public async Task An_employee_checked_in_cannot_be_edited_or_removed()
    {
        var s = await AssignedAsync();
        host.Attendance.CheckIn(s.Employee.Id, Today(), DateTimeOffset.UtcNow.AddMinutes(-5));

        var edit = await EditAsync(s, new { jobTypeId = (await AddJobTypeAsync(host)).Id, rowVersion = s.RowVersion });
        var remove = await s.Manager.DeleteAsync($"/api/work-assignments/{s.Id}?rowVersion={Uri.EscapeDataString(s.RowVersion)}");

        await edit.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-07", null);
        await remove.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-07", null);
        Assert.Single(await host.RowsOfAsync(s.Employee, Today()));
    }

    // A job period starts at a job change after the check-in: the employee is checked in, so it is not the work assigned before it.
    [Fact]
    public async Task A_job_period_is_not_editable()
    {
        var s = await AssignedAsync();
        var period = await StoreAssignmentAsync(host, s.Employee, s.Raft, s.Job, Today(), DateTimeOffset.UtcNow);

        var response = await EditAsync(s, new { raftId = s.Raft.Id, rowVersion = await host.IssueVersionAsync(period.Id) }, period.Id.ToString());

        await response.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-07", null);
    }

    [Fact]
    public async Task Work_of_a_past_date_and_of_an_employee_no_longer_employed_cannot_be_changed()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job, past, left) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]), await AddEmployeeAsync(host, [zone]));
        var yesterday = await StoreAssignmentAsync(host, past, raft, job, Today().AddDays(-1));
        var ended = await StoreAssignmentAsync(host, left, raft, job, Today().AddDays(1));
        await host.UpdateAsync<Employee>(left.Id, e => e.StatusId = StatusIds.Employee.Left);

        var pastEdit = await manager.PutAsJsonAsync($"/api/work-assignments/{yesterday.Id}", new { jobTypeId = job.Id, rowVersion = await host.IssueVersionAsync(yesterday.Id) });
        var leftEdit = await manager.DeleteAsync($"/api/work-assignments/{ended.Id}?rowVersion={Uri.EscapeDataString(await host.IssueVersionAsync(ended.Id))}");

        Assert.Equal("Date", (await pastEdit.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        Assert.Equal("MSG-GEN-09", await leftEdit.ErrorCodeAsync());
    }

    [Fact]
    public async Task Removing_deletes_the_row_keeps_it_in_the_audit_log_and_tells_the_employee()
    {
        var s = await AssignedAsync();
        var removed = await host.AuditCountAsync("WorkAssignment.Removed");

        var response = await s.Manager.DeleteAsync($"/api/work-assignments/{s.Id}?rowVersion={Uri.EscapeDataString(s.RowVersion)}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await host.RowsOfAsync(s.Employee, Today()));
        Assert.Equal(removed + 1, await host.AuditCountAsync("WorkAssignment.Removed"));
        Assert.Contains("is removed", host.Pushes.Notices.Last(n => n.EmployeeId == s.Employee.Id).Message.Text);
        Assert.Equal("MSG-LABOR-05", host.Pushes.Notices.Last(n => n.EmployeeId == s.Employee.Id).Message.Code);
    }

    [Fact]
    public async Task Removing_needs_the_current_version_and_a_known_assignment()
    {
        var s = await AssignedAsync();

        var stale = await s.Manager.DeleteAsync($"/api/work-assignments/{s.Id}?rowVersion={Uri.EscapeDataString(StaleRowVersion)}");
        var none = await s.Manager.DeleteAsync($"/api/work-assignments/{s.Id}");
        var unknown = await s.Manager.DeleteAsync($"/api/work-assignments/{Guid.NewGuid()}?rowVersion={Uri.EscapeDataString(s.RowVersion)}");

        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await none.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Single(await host.RowsOfAsync(s.Employee, Today()));
    }

    [Fact]
    public async Task A_push_that_fails_leaves_the_removal_saved()
    {
        var s = await AssignedAsync();
        host.Pushes.Fails = true;
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await s.Manager.DeleteAsync($"/api/work-assignments/{s.Id}?rowVersion={Uri.EscapeDataString(s.RowVersion)}")).StatusCode);
        }
        finally
        {
            host.Pushes.Fails = false;
        }

        Assert.Empty(await host.RowsOfAsync(s.Employee, Today()));
    }

    [Fact]
    public async Task A_date_held_by_a_locked_pay_period_cannot_be_edited_or_removed()
    {
        await using var own = new WorkAssignmentHost();
        var zone = await own.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(own, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(own, zone), await AddJobTypeAsync(own), await AddEmployeeAsync(own, [zone]));
        var date = Today().AddDays(40);
        var assignment = await StoreAssignmentAsync(own, employee, raft, job, date);
        await own.AddAwaitingPayrollAsync(Iso(date.AddDays(-5)), Iso(date.AddDays(10)));

        var version = await own.IssueVersionAsync(assignment.Id);
        var edit = await manager.PutAsJsonAsync($"/api/work-assignments/{assignment.Id}", new { jobTypeId = job.Id, rowVersion = version });
        var remove = await manager.DeleteAsync($"/api/work-assignments/{assignment.Id}?rowVersion={Uri.EscapeDataString(version)}");

        await edit.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        await remove.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Single(await own.RowsOfAsync(employee, date));
    }
}
