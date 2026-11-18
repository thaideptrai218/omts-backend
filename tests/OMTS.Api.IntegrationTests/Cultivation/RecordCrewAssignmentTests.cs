using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// Decision of 17/11/2026: a nursery batch names its employees (table NurseryRecordEmployees) and, once saved, they get a work assignment in the zone with the job type Ươm. The assignment is a step after
/// the save: it skips anyone who already has work that day, a record of another day, a missing job type and a Left employee, with a reason, and the batch stays saved.
/// </summary>
public sealed class RecordCrewAssignmentTests
{
    private static async Task<Employee> EmployeeAsync(AuthApiFactory api, string name, DateOnly? lastWorkingDay = null)
    {
        var cooperative = await api.EnsureCooperativeAsync();
        var employee = new Employee { CooperativeId = cooperative.Id, FullName = name, Code = $"NV{NewCode()}", StatusId = StatusIds.Employee.Working, LastWorkingDay = lastWorkingDay };
        await api.UseDbAsync(db => db.Employees.Add(employee));
        return employee;
    }

    private static async Task<Guid> NurseryJobTypeAsync(AuthApiFactory api, int statusId = StatusIds.Usage.InUse)
    {
        var job = new JobType { Name = "Ươm", StatusId = statusId };
        await api.UseDbAsync(db => db.JobTypes.Add(job));
        return job.Id;
    }

    private static async Task<JsonElement> StartAsync(NurseryWorld world, NurseryFields fields) =>
        await (await world.Owner.Client.PostNurseryAsync(world.ZoneId, fields with { SeedLotId = world.Lot.LineId })).ReadAsync(HttpStatusCode.Created);

    [Fact]
    public async Task A_nursery_started_with_employees_keeps_them_and_assigns_them_in_the_zone_with_the_nursery_job()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var job = await NurseryJobTypeAsync(api);
        var hoa = await EmployeeAsync(api, "Hoa");
        var an = await EmployeeAsync(api, "An");

        var saved = await StartAsync(world, new NurseryFields { Crew = [hoa.Id, an.Id] });

        Assert.Equal(["An", "Hoa"], saved.GetProperty("batch").GetProperty("crew").EnumerateArray().Select(c => c.Text("name")).ToList());
        Assert.Equal(2, saved.GetProperty("crewAssignment").GetProperty("created").GetInt32());
        Assert.Empty(saved.GetProperty("crewAssignment").GetProperty("skipped").EnumerateArray());
        var rows = await api.QueryAsync(db => db.WorkAssignments.Where(w => w.JobTypeId == job).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, w => Assert.Equal((world.ZoneId, (Guid?)null), (w.ZoneId, w.RaftId)));
        Assert.Equal(2, await api.QueryAsync(db => db.NurseryRecordEmployees.CountAsync(c => c.NurseryBatchId == saved.GetProperty("batch").Id())));
        Assert.Empty(await api.QueryAsync(db => db.Attendances.ToListAsync()));
    }

    [Fact]
    public async Task An_employee_with_work_that_day_is_skipped_and_never_moved_and_the_batch_stays_saved()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var job = await NurseryJobTypeAsync(api);
        var busy = await EmployeeAsync(api, "Busy");
        var free = await EmployeeAsync(api, "Free");
        var today = Today;
        var raft = await api.QueryAsync(db => db.Rafts.SingleAsync(r => r.Id == world.Raft1));
        await api.UseDbAsync(db => db.WorkAssignments.Add(new WorkAssignment
        {
            EmployeeId = busy.Id, Date = today, RaftId = raft.Id, ZoneId = raft.ZoneId, JobTypeId = job, AssignedByAccountId = world.Owner.Account.Id,
        }));

        var saved = await StartAsync(world, new NurseryFields { Crew = [busy.Id, free.Id] });

        var crewAssignment = saved.GetProperty("crewAssignment");
        Assert.Equal(1, crewAssignment.GetProperty("created").GetInt32());
        var skipped = crewAssignment.GetProperty("skipped").EnumerateArray().Single();
        Assert.Equal((busy.Id, "AlreadyAssigned"), (skipped.GetProperty("employeeId").GetGuid(), skipped.Text("reason")));
        Assert.Equal(raft.Id, (await api.QueryAsync(db => db.WorkAssignments.SingleAsync(w => w.EmployeeId == busy.Id))).RaftId);
        Assert.Equal(2, saved.GetProperty("batch").GetProperty("crew").GetArrayLength());
    }

    [Fact]
    public async Task An_employee_who_finished_work_at_the_same_place_is_reopened_by_a_new_record_the_same_day()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        await NurseryJobTypeAsync(api);
        var hoa = await EmployeeAsync(api, "Hoa");
        await StartAsync(world, new NurseryFields { Crew = [hoa.Id] });
        var now = DateTimeOffset.UtcNow;
        await api.UseDbAsync(db => db.Attendances.Add(new Attendance { EmployeeId = hoa.Id, Date = Today, CheckInAt = now.AddHours(-2), CheckOutAt = now.AddHours(-1) }));

        var second = await StartAsync(world, new NurseryFields { Crew = [hoa.Id] });

        Assert.Equal(1, second.GetProperty("crewAssignment").GetProperty("created").GetInt32());
        Assert.Empty(second.GetProperty("crewAssignment").GetProperty("skipped").EnumerateArray());
        var record = await api.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == hoa.Id));
        Assert.Null(record.CheckOutAt);
        Assert.NotNull(record.CheckInAt);
        Assert.Equal(1, await api.QueryAsync(db => db.WorkAssignments.CountAsync(w => w.EmployeeId == hoa.Id)));
    }

    [Fact]
    public async Task A_record_of_another_day_a_missing_job_type_and_an_employee_who_left_are_skipped_with_their_reason()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var hoa = await EmployeeAsync(api, "Hoa");

        // No job type Ươm in the catalogue yet.
        var noJob = await StartAsync(world, new NurseryFields { Crew = [hoa.Id] });
        Assert.Equal(["JobTypeNotInUse"], noJob.GetProperty("crewAssignment").GetProperty("skipped").EnumerateArray().Select(s => s.Text("reason")).ToList());

        await NurseryJobTypeAsync(api);
        var past = await StartAsync(world, new NurseryFields { Crew = [hoa.Id], StartDate = Today.AddDays(-1).ToString("yyyy-MM-dd") });
        Assert.Equal(["PastDate"], past.GetProperty("crewAssignment").GetProperty("skipped").EnumerateArray().Select(s => s.Text("reason")).ToList());

        var gone = await EmployeeAsync(api, "Gone", lastWorkingDay: Today.AddDays(-1));
        var left = await StartAsync(world, new NurseryFields { Crew = [gone.Id] });
        Assert.Equal(["Left"], left.GetProperty("crewAssignment").GetProperty("skipped").EnumerateArray().Select(s => s.Text("reason")).ToList());
        Assert.Equal(0, await api.QueryAsync(db => db.WorkAssignments.CountAsync()));
        Assert.Equal(3, await api.QueryAsync(db => db.NurseryBatches.CountAsync()));
    }

    [Fact]
    public async Task The_crew_is_validated_and_an_edit_changes_it_only_when_it_is_sent()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var hoa = await EmployeeAsync(api, "Hoa");
        var batch = (await StartAsync(world, new NurseryFields { Crew = [hoa.Id] })).GetProperty("batch");

        var unknown = await world.Owner.Client.PostNurseryAsync(world.ZoneId, new NurseryFields { SeedLotId = world.Lot.LineId, Crew = [Guid.NewGuid()] });
        Assert.Equal(["Crew"], await unknown.InvalidFieldsAsync());

        var kept = await (await world.Owner.Client.PutNurseryAsync(batch.Id(), new NurseryFields { Note = "x", RowVersion = await api.IssueRowVersionAsync<OMTS.Domain.Entities.Cultivation.NurseryBatch>(batch.Id()) })).ReadAsync(HttpStatusCode.OK);
        Assert.Single(kept.GetProperty("batch").GetProperty("crew").EnumerateArray());
        Assert.False(kept.TryGetProperty("crewAssignment", out _));

        var cleared = await (await world.Owner.Client.PutNurseryAsync(batch.Id(), new NurseryFields { ReplaceCrew = true, RowVersion = await api.IssueRowVersionAsync<OMTS.Domain.Entities.Cultivation.NurseryBatch>(batch.Id()) })).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(cleared.GetProperty("batch").GetProperty("crew").EnumerateArray());
        Assert.Equal(0, await api.QueryAsync(db => db.NurseryRecordEmployees.CountAsync()));
    }

    [Fact]
    public async Task A_nursery_cannot_be_completed_until_its_assigned_crew_is_checked_out_unless_the_owner_approves_with_a_reason()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        await NurseryJobTypeAsync(api);
        var hoa = await EmployeeAsync(api, "Hoa");
        var batch = (await StartAsync(world, new NurseryFields { Crew = [hoa.Id] })).GetProperty("batch");
        var client = world.Owner.Client;

        var blocked = await client.PostCompleteAsync(api, batch.Id(), "900");
        var problem = await blocked.ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-LABOR-37", "ATTENDANCE_OPEN", "ownerApproved"), (problem.Text("code"), problem.Text("reasonCode"), problem.Text("confirmFlag")));
        var listed = problem.GetProperty("employees").EnumerateArray().Single();
        Assert.Equal((hoa.Id, false), (listed.Id(), listed.GetProperty("checkedIn").GetBoolean()));
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches/{batch.Id()}");
        Assert.Equal("NotCheckedIn", read.GetProperty("crew")[0].Text("attendance"));

        var noReason = await client.PostCompleteAsync(api, batch.Id(), "900", ownerApproved: true);
        Assert.Equal(["ApprovalReason"], await noReason.InvalidFieldsAsync());
        var approved = await client.PostCompleteAsync(api, batch.Id(), "900", ownerApproved: true, approvalReason: "Quên bấm ra");
        await approved.ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Record.CompletedWithOpenAttendance" && e.RecordId == batch.Id() && e.Reason == "Quên bấm ra")));
    }

    [Fact]
    public async Task A_checked_out_crew_and_a_member_with_no_work_that_day_do_not_block_and_the_view_shows_the_work_time()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        await NurseryJobTypeAsync(api);
        var hoa = await EmployeeAsync(api, "Hoa");
        var batch = (await StartAsync(world, new NurseryFields { Crew = [hoa.Id] })).GetProperty("batch");
        var start = new DateTimeOffset(Today.ToDateTime(new TimeOnly(7, 0)), TimeSpan.FromHours(7));
        await api.UseDbAsync(db => db.Attendances.Add(new Attendance { EmployeeId = hoa.Id, Date = Today, CheckInAt = start, CheckOutAt = start.AddMinutes(150) }));

        var read = await world.Owner.Client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches/{batch.Id()}");
        var member = read.GetProperty("crew")[0];
        Assert.Equal(("Done", 150), (member.Text("attendance"), member.GetProperty("work").GetProperty("minutes").GetInt32()));
        await (await world.Owner.Client.PostCompleteAsync(api, batch.Id(), "900")).ReadAsync(HttpStatusCode.OK);

        // A back-dated batch gave nobody an assignment, so its crew is NotAssigned and never blocks.
        var past = (await StartAsync(world, new NurseryFields { Crew = [hoa.Id], StartDate = Today.AddDays(-1).ToString("yyyy-MM-dd") })).GetProperty("batch");
        Assert.Equal("NotAssigned", past.GetProperty("crew")[0].Text("attendance"));
        await (await world.Owner.Client.PostCompleteAsync(api, past.Id(), "900")).ReadAsync(HttpStatusCode.OK);
    }
}
