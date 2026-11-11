using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// QA probes for L4 (LABOR-02, commit 4909565e). Each probe states the SRS behaviour; they failed on 4909565e and pass with the
/// fixes of M1 to M3 (report qa-261111-l4-assign-work.md, "Fixes").
/// </summary>
public class WorkAssignmentQaTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    // Finding M1 (BR-LABOR-01: "their work assignments after the last working day are removed"). An end of employment with a last
    // working day before today makes the employee Left at once. An assign that read the employee as Employed and commits after the end
    // leaves a row the end action did not see; the daily job only looks at employees still Employed, so it never removes it.
    [Fact]
    public async Task An_assignment_that_raced_an_end_of_employment_in_the_past_is_removed_by_the_daily_job()
    {
        await using var own = new WorkAssignmentHost();
        var zone = await own.AddZoneAsync(NewCode());
        var owner = await OwnerAsync(own);
        var (raft, job, employee) = (await AddRaftAsync(own, zone), await AddJobTypeAsync(own), await AddEmployeeAsync(own, [zone]));
        await OMTS.Api.IntegrationTests.Employees.EmployeeRequests.EndAsync(owner, employee.Id, Today().AddDays(-1));
        // The row an assign racing the end would leave: the assign checked the employee before the end saved, and saved after it.
        await StoreAssignmentAsync(own, employee, raft, job, Today().AddDays(3));

        await own.Services.GetRequiredService<EmploymentEndJob>().RunOnceAsync(Today().AddDays(1));

        Assert.Empty(await own.RowsOfAsync(employee, Today().AddDays(3)));
    }

    // Finding M2 (LABOR-02.2 step 3 "Return to step 9", which adds the zone of the raft to the working zones; POST-1). A PUT that
    // moves the work to a raft of another zone must give the employee that zone, as the assign does.
    [Fact]
    public async Task A_put_that_moves_the_work_to_a_raft_of_another_zone_adds_that_zone_to_the_working_zones()
    {
        var (zoneA, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var owner = await OwnerAsync(host);
        var (raftA, raftB, job) = (await AddRaftAsync(host, zoneA), await AddRaftAsync(host, zoneB), await AddJobTypeAsync(host));
        var employee = await AddEmployeeAsync(host, [zoneA]);
        var id = (await owner.AssignAsync(raftA, Today().AddDays(1), job, [employee]).BodyAsync(HttpStatusCode.Created)).First().GetProperty("id").GetGuid();
        var version = await host.IssueVersionAsync(id);

        await (await owner.PutAsJsonAsync($"/api/work-assignments/{id}", new { raftId = raftB.Id, rowVersion = version })).BodyAsync(HttpStatusCode.OK);

        var zones = await host.QueryAsync(db => db.Employees.Where(e => e.Id == employee.Id).SelectMany(e => e.WorkingZones.Select(w => w.ZoneId)).ToListAsync());
        Assert.Contains(zoneB.Id, zones);
    }

    // Finding M3 (BR-LABOR-05: a job period ends at the next job period). The work day of a Zone Manager of zone A computes the hours
    // from all periods of the employee, so a period followed by a job change to a raft of zone B ends there, and only the periods on
    // rafts of zone A are counted on that screen. (The probe was changed: the employee returns to raft A at 13:00, because a member
    // whose latest period is on a raft of zone B is not on the crew of zone A, so there is no member to read hours from.)
    [Fact]
    public async Task The_work_day_of_a_zone_manager_ends_a_period_where_the_employee_moved_to_a_raft_of_another_zone()
    {
        await using var own = new WorkAssignmentHost();
        var (zoneA, zoneB) = (await own.AddZoneAsync(NewCode()), await own.AddZoneAsync(NewCode()));
        var managerA = await StaffInZoneAsync(own, StaffRole.ZoneManager, zoneA);
        var (raftA, raftB) = (await AddRaftAsync(own, zoneA), await AddRaftAsync(own, zoneB));
        var (harvesting, hanging) = (await AddJobTypeAsync(own), await AddJobTypeAsync(own));
        var employee = await AddEmployeeAsync(own, [zoneA, zoneB]);
        var (date, vietnam) = (new DateOnly(2026, 3, 10), TimeSpan.FromHours(7));
        DateTimeOffset At(int hour) => new(date.Year, date.Month, date.Day, hour, 0, 0, vietnam);
        await StoreAssignmentAsync(own, employee, raftA, harvesting, date);
        await StoreAssignmentAsync(own, employee, raftB, hanging, date, At(10));
        await StoreAssignmentAsync(own, employee, raftA, hanging, date, At(13));
        own.Attendance.CheckIn(employee.Id, date, At(6), At(16));

        var day = await managerA.GetFromJsonAsync<JsonElement>($"/api/work-days/{Iso(date)}");

        var member = CrewMember(day, employee);
        int Minutes(JobType job) => member.GetProperty("hours").EnumerateArray()
            .Single(h => h.GetProperty("jobTypeId").GetGuid() == job.Id).GetProperty("minutes").GetInt32();
        // 06:00-10:00 harvesting on raft A (ends where the employee moved to raft B); 10:00-13:00 on raft B is not shown; 13:00-16:00 hanging on raft A.
        // No setting of this host takes a break or rounds.
        Assert.Equal((240, 180), (Minutes(harvesting), Minutes(hanging)));
        Assert.Equal(2, member.GetProperty("periods").GetArrayLength());
    }

    // Finding M3 (a): the member is on the crew of the raft of the latest period, not left on the old crew; with that raft in zone B
    // the member is not on the screen of the Zone Manager of zone A.
    [Fact]
    public async Task A_member_whose_latest_period_is_on_a_raft_of_another_zone_is_not_left_on_the_old_crew()
    {
        await using var own = new WorkAssignmentHost();
        var (zoneA, zoneB) = (await own.AddZoneAsync(NewCode()), await own.AddZoneAsync(NewCode()));
        var managerA = await StaffInZoneAsync(own, StaffRole.ZoneManager, zoneA);
        var (raftA, raftB) = (await AddRaftAsync(own, zoneA), await AddRaftAsync(own, zoneB));
        var (harvesting, hanging) = (await AddJobTypeAsync(own), await AddJobTypeAsync(own));
        var (employee, stayer) = (await AddEmployeeAsync(own, [zoneA, zoneB]), await AddEmployeeAsync(own, [zoneA]));
        var date = Today();
        await StoreAssignmentAsync(own, employee, raftA, harvesting, date);
        await StoreAssignmentAsync(own, employee, raftB, hanging, date, DateTimeOffset.UtcNow.AddHours(-1));
        await StoreAssignmentAsync(own, stayer, raftA, harvesting, date);
        own.Attendance.CheckIn(employee.Id, date, DateTimeOffset.UtcNow.AddHours(-3));

        var day = await managerA.GetFromJsonAsync<JsonElement>($"/api/work-days/{Iso(date)}");

        var crew = day.GetProperty("rafts").EnumerateArray()
            .SelectMany(r => r.GetProperty("crew").EnumerateArray()).Select(m => m.GetProperty("employeeId").GetGuid()).ToList();
        Assert.DoesNotContain(employee.Id, crew);
        Assert.Contains(stayer.Id, crew);
        // Nobody of this crew is at work, so the Zone Manager of zone A cannot change the job of a crew whose member is gone.
        Assert.False(day.GetProperty("rafts").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == raftA.Id).GetProperty("canChangeJob").GetBoolean());
    }

    // Finding M1: the sweep keeps the rows up to the last working day, removes later ones of Employed and Left employees, and can run again.
    [Fact]
    public async Task The_daily_job_removes_only_the_assignments_after_the_last_working_day_and_again_changes_nothing()
    {
        await using var own = new WorkAssignmentHost();
        var zone = await own.AddZoneAsync(NewCode());
        // The stored rows name an account that assigned them: the Owner of this host.
        await OwnerAsync(own);
        var (raft, job) = (await AddRaftAsync(own, zone), await AddJobTypeAsync(own));
        var (pending, none) = (await AddEmployeeAsync(own, [zone], change: e => e.LastWorkingDay = Today().AddDays(10)), await AddEmployeeAsync(own, [zone]));
        await StoreAssignmentAsync(own, pending, raft, job, Today().AddDays(10));
        await StoreAssignmentAsync(own, pending, raft, job, Today().AddDays(11));
        await StoreAssignmentAsync(own, none, raft, job, Today().AddDays(11));
        var endJob = own.Services.GetRequiredService<EmploymentEndJob>();

        await endJob.RunOnceAsync(Today());
        await endJob.RunOnceAsync(Today());

        Assert.Single(await own.RowsOfAsync(pending, Today().AddDays(10)));
        Assert.Empty(await own.RowsOfAsync(pending, Today().AddDays(11)));
        Assert.Single(await own.RowsOfAsync(none, Today().AddDays(11)));
    }

    private static JsonElement CrewMember(JsonElement day, Employee employee) =>
        day.GetProperty("rafts").EnumerateArray()
            .SelectMany(r => r.GetProperty("crew").EnumerateArray())
            .Single(m => m.GetProperty("employeeId").GetGuid() == employee.Id);
}
