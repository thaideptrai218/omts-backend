using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// BR-LABOR-01 and LABOR-01.3: the work assignments after the last working day of an employee are removed, by the end action and again by the daily
/// job (an employee ended today stays Employed until the next day and can still be assigned meanwhile). Their other days stay.
/// </summary>
public class WorkAssignmentEmploymentEndTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    private static Task<JsonElement> EndAsync(HttpClient owner, Employee employee, DateOnly lastWorkingDay) =>
        OMTS.Api.IntegrationTests.Employees.EmployeeRequests.EndAsync(owner, employee.Id, lastWorkingDay);

    /// <summary>How many rows the employee has on each day, the days given as offsets from today.</summary>
    private static async Task<int[]> RowCountsAsync(AuthApiFactory factory, Employee employee, params int[] offsets)
    {
        var counts = new List<int>();
        foreach (var offset in offsets)
        {
            counts.Add((await factory.RowsOfAsync(employee, Today().AddDays(offset))).Length);
        }

        return [.. counts];
    }

    [Fact]
    public async Task Ending_the_employment_removes_the_assignments_and_job_periods_after_the_last_working_day_only()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var owner = await OwnerAsync(host);
        var (raft, job) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host));
        var (ending, other) = (await AddEmployeeAsync(host, [zone]), await AddEmployeeAsync(host, [zone]));
        foreach (var day in new[] { Today().AddDays(-1), Today(), Today().AddDays(1), Today().AddDays(5) })
        {
            await StoreAssignmentAsync(host, ending, raft, job, day);
            await StoreAssignmentAsync(host, other, raft, job, day);
        }

        await StoreAssignmentAsync(host, ending, raft, job, Today().AddDays(1), DateTimeOffset.UtcNow.AddDays(1));
        await EndAsync(owner, ending, Today());

        Assert.Equal([1, 1, 0, 0], await RowCountsAsync(host, ending, -1, 0, 1, 5));
        Assert.Equal([1, 1, 1, 1], await RowCountsAsync(host, other, -1, 0, 1, 5));
    }

    [Fact]
    public async Task An_employee_left_at_once_loses_the_assignments_of_today_too()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var owner = await OwnerAsync(host);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        await StoreAssignmentAsync(host, employee, raft, job, Today().AddDays(-2));
        await StoreAssignmentAsync(host, employee, raft, job, Today());

        await EndAsync(owner, employee, Today().AddDays(-1));

        Assert.Equal([1, 0], await RowCountsAsync(host, employee, -2, 0));
    }

    [Fact]
    public async Task The_daily_job_removes_what_was_assigned_after_the_end_action_ran()
    {
        await using var own = new WorkAssignmentHost();
        var zone = await own.AddZoneAsync(NewCode());
        var owner = await OwnerAsync(own);
        var manager = await StaffInZoneAsync(own, StaffRole.ZoneManager, zone);
        var (raft, job, employee) = (await AddRaftAsync(own, zone), await AddJobTypeAsync(own), await AddEmployeeAsync(own, [zone]));
        await EndAsync(owner, employee, Today());
        // Today is still a working day, so work can be assigned for it; the day after the last working day is refused at once.
        Assert.Equal(HttpStatusCode.Created, (await manager.AssignAsync(raft, Today(), job, [employee])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.AssignAsync(raft, Today().AddDays(1), job, [employee])).StatusCode);
        // An assignment of a later day that got in meanwhile, as a save that raced the end action would leave it.
        await StoreAssignmentAsync(own, employee, raft, job, Today().AddDays(2));

        Assert.Equal(1, await own.Services.GetRequiredService<EmploymentEndJob>().RunOnceAsync(Today().AddDays(1)));

        Assert.Equal([1, 0], await RowCountsAsync(own, employee, 0, 2));
    }
}
