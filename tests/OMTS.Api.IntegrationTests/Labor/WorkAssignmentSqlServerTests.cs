using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LABOR-02 on a real SQL Server: the filtered unique index keeps one work assignment per employee before the check-in whatever the saves race on,
/// the row version keeps one winner of parallel edits, and a send of the payroll cannot pass an assignment unseen. A loser answers 409, never 500.
/// </summary>
public class WorkAssignmentSqlServerTests
{
    private const int Parallel = 8;
    private const string Index = "UX_WorkAssignments_EmployeeId_WorkDate_FirstPeriod";

    private sealed record World(SqlServerApiFactory Api, HttpClient Owner, Cooperative Cooperative, FarmingZone Zone, JobType Job);

    private static async Task<World> WorldAsync(SqlServerApiFactory api)
    {
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await FarmLabSqlServerKit.AddZoneAsync(api, cooperative.Id);
        var job = new JobType { Name = $"Job {NewCode()}" };
        await ChangeAsync(api, db => db.JobTypes.Add(job));
        return new World(api, owner, cooperative, zone, job);
    }

    private static async Task<Raft> RaftAsync(World w)
    {
        var raft = new Raft { ZoneId = w.Zone.Id, Code = $"B-{NewCode()}" };
        await ChangeAsync(w.Api, db => db.Rafts.Add(raft));
        return raft;
    }

    private static async Task<Employee> EmployeeAsync(World w)
    {
        var employee = new Employee
        {
            CooperativeId = w.Cooperative.Id,
            FullName = $"Nhân viên {NewCode()}",
            WorkingZones = [new EmployeeWorkingZone { ZoneId = w.Zone.Id }],
            TypePeriods = [new EmployeeTypePeriod { Type = EmployeeType.SeasonalWorker, EffectiveFrom = new DateOnly(2020, 1, 1) }],
        };
        await ChangeAsync(w.Api, db => db.Employees.Add(employee));
        return employee;
    }

    private static Task<int> RowsAsync(World w, Employee employee, DateOnly date) =>
        QueryAsync(w.Api, db => db.WorkAssignments.CountAsync(a => a.EmployeeId == employee.Id && a.WorkDate == date));

    [SqlServerFact]
    public async Task Parallel_assignments_of_one_employee_on_one_date_leave_one_assignment()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var w = await WorldAsync(api);
        var employee = await EmployeeAsync(w);
        var rafts = new List<Raft>();
        for (var i = 0; i < Parallel; i++)
        {
            rafts.Add(await RaftAsync(w));
        }

        var responses = await InParallelAsync(Parallel, i => w.Owner.AssignAsync(rafts[i], Today(), w.Job, [employee], moveConfirmed: i % 2 == 0));

        await AssertNo500Async(responses);
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Contains(await loser.ErrorCodeAsync(), new[] { "MSG06", "MSG-LABOR-06" });
        }

        Assert.Equal(1, await RowsAsync(w, employee, Today()));
    }

    [SqlServerFact]
    public async Task Parallel_assignments_of_different_employees_to_one_raft_are_all_saved()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var w = await WorldAsync(api);
        var raft = await RaftAsync(w);
        var employees = new List<Employee>();
        for (var i = 0; i < Parallel; i++)
        {
            employees.Add(await EmployeeAsync(w));
        }

        var responses = await InParallelAsync(Parallel, i => w.Owner.AssignAsync(raft, Today(), w.Job, [employees[i]]));

        await AssertNo500Async(responses);
        Assert.Equal(responses.Count(r => r.StatusCode == HttpStatusCode.Created), await QueryAsync(api, db => db.WorkAssignments.CountAsync(a => a.RaftId == raft.Id)));
        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.Created) >= 1);
    }

    [SqlServerFact]
    public async Task The_index_holds_one_assignment_before_the_check_in_and_allows_many_job_periods()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var w = await WorldAsync(api);
        var (raft, employee) = (await RaftAsync(w), await EmployeeAsync(w));
        var by = (await QueryAsync(api, db => db.Accounts.Select(a => a.Id).FirstAsync()));
        WorkAssignment Row(DateTimeOffset? start) => new()
        {
            EmployeeId = employee.Id, RaftId = raft.Id, JobTypeId = w.Job.Id, WorkDate = Today(), StartTime = start, AssignedByAccountId = by,
        };
        await ChangeAsync(api, db => db.WorkAssignments.Add(Row(null)));

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => ChangeAsync(api, db => db.WorkAssignments.Add(Row(null))));
        await ChangeAsync(api, db => db.WorkAssignments.AddRange(Row(DateTimeOffset.UtcNow), Row(DateTimeOffset.UtcNow.AddMinutes(30))));

        Assert.Contains(Index, duplicate.ToString());
        Assert.Equal(3, await RowsAsync(w, employee, Today()));
        Assert.Equal(
            "([StartTime] IS NULL)",
            await QueryAsync(api, db => db.Database.SqlQueryRaw<string>("SELECT filter_definition AS [Value] FROM sys.indexes WHERE name = '" + Index + "'").SingleAsync()));
    }

    [SqlServerFact]
    public async Task Parallel_edits_of_one_assignment_with_one_version_leave_one_winner()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var w = await WorldAsync(api);
        var (raft, employee) = (await RaftAsync(w), await EmployeeAsync(w));
        var created = (await w.Owner.AssignAsync(raft, Today(), w.Job, [employee]).BodyAsync(HttpStatusCode.Created)).First();
        var (id, version) = (created.GetProperty("id").GetGuid(), created.RowVersionOf());
        var targets = new List<Raft>();
        for (var i = 0; i < Parallel; i++)
        {
            targets.Add(await RaftAsync(w));
        }

        var responses = await InParallelAsync(Parallel, i => w.Owner.PutAsJsonAsync($"/api/work-assignments/{id}", new { raftId = targets[i].Id, rowVersion = version }));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
        Assert.Equal(1, await RowsAsync(w, employee, Today()));
    }

    [SqlServerFact]
    public async Task An_edit_and_a_removal_with_one_version_leave_one_winner()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var w = await WorldAsync(api);
        var (raft, other, employee) = (await RaftAsync(w), await RaftAsync(w), await EmployeeAsync(w));
        var created = (await w.Owner.AssignAsync(raft, Today(), w.Job, [employee]).BodyAsync(HttpStatusCode.Created)).First();
        var (id, version) = (created.GetProperty("id").GetGuid(), created.RowVersionOf());

        var responses = await Task.WhenAll(
            w.Owner.PutAsJsonAsync($"/api/work-assignments/{id}", new { raftId = other.Id, rowVersion = version }),
            w.Owner.DeleteAsync($"/api/work-assignments/{id}?rowVersion={Uri.EscapeDataString(version)}"));

        // Whichever saves first wins; a late one finds the row gone (404) or changed (MSG06), never a server error.
        Assert.All(responses, r => Assert.True((int)r.StatusCode < 500, r.StatusCode.ToString()));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
    }

    // QA M4: the job period in force is saved as changed, so its row version serializes parallel changes of one member: a loser that read
    // the same period answers MSG06, and every saved change started from the period of the one before it. (Which loser reads late and
    // succeeds in turn depends on timing, so the test holds the invariants and not a fixed count.)
    [SqlServerFact]
    public async Task Parallel_job_changes_of_one_member_save_a_period_only_from_the_period_they_read()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var w = await WorldAsync(api);
        var (raft, employee) = (await RaftAsync(w), await EmployeeAsync(w));
        var jobs = new List<JobType>();
        for (var i = 0; i < Parallel; i++)
        {
            var job = new JobType { Name = $"Job {NewCode()}" };
            await ChangeAsync(api, db => db.JobTypes.Add(job));
            jobs.Add(job);
        }

        await w.Owner.AssignAsync(raft, Today(), w.Job, [employee]).BodyAsync(HttpStatusCode.Created);
        // The attendance of this test: the member checked in two hours ago. The other tests of this class use the empty reader.
        var attendance = new FakeAttendanceDayReader();
        attendance.CheckIn(employee.Id, Today(), DateTimeOffset.UtcNow.AddHours(-2));
        using var host = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAttendanceDayReader>();
            services.AddSingleton<IAttendanceDayReader>(attendance);
        }));
        using var crew = host.CreateClient();
        crew.DefaultRequestHeaders.Authorization = w.Owner.DefaultRequestHeaders.Authorization;

        var responses = await InParallelAsync(Parallel, i => crew.ChangeJobAsync(raft, jobs[i], [employee]));

        await AssertNo500Async(responses);
        var saved = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.True(saved >= 1);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal("MSG06", await loser.ErrorCodeAsync());
        }

        var rows = await QueryAsync(api, db => db.WorkAssignments.Where(a => a.EmployeeId == employee.Id && a.WorkDate == Today()).ToListAsync());
        Assert.Equal(1 + saved, rows.Count);
        Assert.Equal(rows.Count - 1, rows.Where(a => a.StartTime is not null).Select(a => a.StartTime).Distinct().Count());
    }

    // BR-LABOR-13: an assignment and the send of the payroll of its period cannot both pass unseen, whichever saves first.
    [SqlServerFact]
    public async Task An_assignment_saved_before_a_send_that_read_the_payroll_earlier_fails_that_send()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var w = await WorldAsync(api);
        var (raft, employee) = (await RaftAsync(w), await EmployeeAsync(w));
        var start = Today().AddDays(30);
        var payroll = new Payroll { PeriodStart = start, PeriodEnd = start.AddDays(29) };
        await ChangeAsync(api, db => db.Payrolls.Add(payroll));
        using var scope = api.Services.CreateScope();
        var sending = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Payrolls.SingleAsync(p => p.Id == payroll.Id);
        sending.StatusId = StatusIds.Payroll.AwaitingFinalization;

        var response = await w.Owner.AssignAsync(raft, start.AddDays(3), w.Job, [employee]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
    }
}
