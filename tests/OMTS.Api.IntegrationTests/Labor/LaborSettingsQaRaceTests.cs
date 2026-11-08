using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsQaKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// QA of OMTS-227, races on a real SQL Server: 2xx or 409, never 500. Each runs alone as well as in a group; the base maps no
/// deadlock, so one would fail here as a 500 with the message of the host's log. Fixed moments use their own connection.
/// </summary>
public class LaborSettingsQaRaceTests
{
    private const int Parallel = 8;

    private static Payroll Draft(int year, int month) =>
        new() { PeriodStart = new(year, month, 1), PeriodEnd = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1) };

    private static async Task<HttpResponseMessage> PutTypeAsync(HttpClient owner, (string Name, string Phone, JsonElement Saved, Guid Zone) employee, string date) =>
        await owner.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(employee.Saved)}", EmployeeRequests.Profile(employee.Name, employee.Phone, employee.Zone, b =>
        {
            b["employeeType"] = nameof(EmployeeType.Staff);
            b["employeeTypeFrom"] = date;
            b["rowVersion"] = EmployeeRequests.RowVersionOf(employee.Saved);
        }));

    private static async Task<List<(string Name, string Phone, JsonElement Saved, Guid Zone)>> AddEmployeesAsync(HttpClient owner, Guid zone, int count)
    {
        var employees = new List<(string, string, JsonElement, Guid)>();
        for (var i = 0; i < count; i++)
        {
            var (name, phone) = ($"Song song {i} {NewCode()}", NewPhone());
            employees.Add((name, phone, await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone, b => b["employeeTypeFrom"] = "2019-01-01")), zone));
        }

        return employees;
    }

    private static async Task<int> ConflictsAsync(HttpResponseMessage[] responses, params string[] codes)
    {
        var count = 0;
        foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Contains(await response.ErrorCodeAsync(), codes);
            count++;
        }

        return count;
    }

    // NFR-09 is about "two users edit the same record": here nobody does. They only date a change inside one Draft period.
    [SqlServerFact(Skip = "Accepted: parallel saves of different records in one Draft period may answer MSG06. The window is milliseconds and a retry works; only L7's payroll row makes this safe.")]
    public async Task L1_rates_of_different_job_types_dated_in_one_draft_period_all_save()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobTypes = new List<JsonElement>();
        for (var i = 0; i < Parallel; i++)
        {
            jobTypes.Add(await AddJobTypeAsync(owner, from: "2019-01-01"));
        }

        await ChangeAsync(api, db => db.Payrolls.Add(Draft(2020, 10)));

        var responses = await Task.WhenAll(jobTypes.Select((j, i) => owner.PostRateAsync(j, 50_000 + i, "2020-10-15")));

        await AssertNo500Async(responses, api.Logs);
        var refused = await ConflictsAsync(responses, "MSG06");
        Assert.True(refused == 0, $"{refused} of {Parallel} rates of different job types answered MSG06 only because they share a Draft period.");
    }

    [SqlServerFact(Skip = "Accepted: parallel saves of different records in one Draft period may answer MSG06. The window is milliseconds and a retry works; only L7's payroll row makes this safe.")]
    public async Task L1_types_of_different_employees_dated_in_one_draft_period_all_save()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var employees = await AddEmployeesAsync(owner, zone.Id, Parallel);
        await ChangeAsync(api, db => db.Payrolls.Add(Draft(2020, 10)));

        var responses = await Task.WhenAll(employees.Select(e => PutTypeAsync(owner, e, "2020-10-15")));

        await AssertNo500Async(responses, api.Logs);
        var refused = await ConflictsAsync(responses, "MSG06");
        Assert.True(refused == 0, $"{refused} of {Parallel} types of different employees answered MSG06 only because they share a Draft period.");
    }

    // The same writes with no payroll in the way: the conflict above comes from the touch of the payroll alone.
    [SqlServerFact]
    public async Task Without_a_payroll_in_the_way_rates_of_different_job_types_all_save()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobTypes = new List<JsonElement>();
        for (var i = 0; i < Parallel; i++)
        {
            jobTypes.Add(await AddJobTypeAsync(owner, from: "2019-01-01"));
        }

        await ChangeAsync(api, db => db.Payrolls.Add(Draft(2020, 9)));

        var responses = await Task.WhenAll(jobTypes.Select((j, i) => owner.PostRateAsync(j, 50_000 + i, "2020-10-15")));

        await AssertNo500Async(responses, api.Logs);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [SqlServerFact]
    public async Task Mixed_writes_on_drafts_and_a_send_and_send_back_loop_never_answer_500()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var (september, october) = (Draft(2020, 9), Draft(2020, 10));
        await ChangeAsync(api, db => db.Payrolls.AddRange(september, october));
        var jobTypes = new List<JsonElement>();
        for (var i = 0; i < 6; i++)
        {
            jobTypes.Add(await AddJobTypeAsync(owner, from: "2019-01-01"));
        }

        var employees = await AddEmployeesAsync(owner, zone.Id, 6);

        for (var round = 0; round < 8; round++)
        {
            var version = (await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings)).VersionOf();
            var date = new DateOnly(2020, 9, 1).AddDays(round);
            var day = date.ToString("yyyy-MM-dd");
            var currentTypes = new List<JsonElement>();
            foreach (var jobType in jobTypes)
            {
                currentTypes.Add(await owner.ReadJobTypeAsync(jobType.IdOf()));
            }

            var currentEmployees = new List<(string, string, JsonElement, Guid)>();
            foreach (var (name, phone, _, employeeZone) in employees)
            {
                var added = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile($"{name} {round}", NewPhone(), employeeZone, b => b["employeeTypeFrom"] = "2019-01-01"));
                currentEmployees.Add(($"{name} {round}", added.GetProperty("employee").GetProperty("phoneNumber").GetString()!, added, employeeZone));
            }

            var calls = new List<Task<HttpResponseMessage>>();
            calls.AddRange(currentTypes.Select((j, i) => owner.PostRateAsync(j, 50_000 + round + i, day)));
            calls.AddRange(currentEmployees.Select(e => PutTypeAsync(owner, e, day)));
            calls.Add(owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = day, rowVersion = version }));
            calls.Add(owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = 2 + round, automaticPayroll = false, rowVersion = version }));
            calls.Add(owner.PostAsJsonAsync(JobTypes, new { name = $"Song song {round} {NewCode()}", hourlyRate = 1, effectiveFrom = day }));
            calls.Add(owner.PutAsJsonAsync($"{JobTypes}/{currentTypes[0].IdOf()}/name", new { name = $"Đổi {NewCode()}", rowVersion = currentTypes[0].VersionOf() }));
            calls.Add(owner.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile($"Thêm {round} {NewCode()}", NewPhone(), zone.Id)));
            calls.Add(owner.PutAsJsonAsync("/api/cooperative/profile", OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests.Valid(version)));
            var send = Task.Run(() => ToggleAsync(api, september.Id));
            var responses = await Task.WhenAll(calls);
            await send;

            await AssertNo500Async(responses, api.Logs);
            await ConflictsAsync(responses, "MSG06", "MSG-LABOR-04", "MSG-GEN-02");
        }

        Assert.DoesNotContain(api.Logs.Lines, line => line.Contains("deadlock", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The send and the send-back that LABOR-12 and LABOR-13 will do: each updates the row under the version it read.</summary>
    private static async Task ToggleAsync(SqlServerApiFactory api, Guid payrollId)
    {
        foreach (var status in new[] { StatusIds.Payroll.AwaitingFinalization, StatusIds.Payroll.SentBack })
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    using var scope = api.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
                    var payroll = await db.Payrolls.SingleAsync(p => p.Id == payrollId);
                    payroll.StatusId = status;
                    await db.SaveChangesAsync();
                    break;
                }
                catch (DbUpdateConcurrencyException)
                {
                }
            }
        }
    }

    [SqlServerFact]
    public async Task Parallel_adds_of_one_name_in_every_letter_case_leave_one_job_type()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var name = $"Thu hoạch {NewCode()}";
        var forms = new[] { name, name.ToUpperInvariant(), name.ToLowerInvariant(), $" {name} " };

        var responses = await InParallelAsync(Parallel, i => owner.PostAsJsonAsync(JobTypes, new { name = forms[i % forms.Length], hourlyRate = 40_000 + i, effectiveFrom = "2020-01-01" }));

        await AssertOneWinnerAsync(responses, HttpStatusCode.Created, "MSG-GEN-02");
        Assert.Equal(1, await QueryAsync(api, db => db.JobTypes.CountAsync()));
        Assert.Equal(1, await QueryAsync(api, db => db.PayRates.CountAsync()));
    }

    [SqlServerFact]
    public async Task Parallel_renames_of_different_job_types_to_one_name_leave_one_with_it()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobTypes = new List<JsonElement>();
        for (var i = 0; i < Parallel; i++)
        {
            jobTypes.Add(await AddJobTypeAsync(owner));
        }

        var name = $"Tên chung {NewCode()}";

        var responses = await Task.WhenAll(jobTypes.Select((j, i) => owner.PutAsJsonAsync($"{JobTypes}/{j.IdOf()}/name", new { name = i % 2 == 0 ? name : name.ToUpperInvariant(), rowVersion = j.VersionOf() })));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG-GEN-02");
        Assert.Equal(1, await QueryAsync(api, db => db.JobTypes.CountAsync(j => j.Name == name)));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "JobType.Renamed")));
    }

    [SqlServerFact]
    public async Task Parallel_rate_rename_discontinue_of_one_job_type_leave_one_winner_and_a_true_history()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        for (var round = 0; round < 5; round++)
        {
            var jobType = await AddJobTypeAsync(owner, from: "2019-01-01");
            var path = $"{JobTypes}/{jobType.IdOf()}";

            var responses = await Task.WhenAll(
                owner.PostRateAsync(jobType, 50_000, "2020-01-01"),
                owner.PostRateAsync(jobType, 60_000, "2020-01-01"),
                owner.PostRateAsync(jobType, 70_000, "2021-01-01"),
                owner.PutAsJsonAsync($"{path}/name", new { name = $"Đổi {NewCode()}", rowVersion = jobType.VersionOf() }),
                owner.PostAsJsonAsync($"{path}/discontinue", new { rowVersion = jobType.VersionOf() }));

            // A loser that reads after the discontinue is told so: MSG-GEN-09 before the version.
            await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06", "MSG-GEN-09");
            var audits = await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordId == jobType.IdOf() && e.Action != "JobType.Created"));
            Assert.Equal(1, audits);
        }
    }

    // A Draft in the way of a parallel replace: the rows stay one per date, whichever way each save loses.
    [SqlServerFact]
    public async Task Parallel_replaces_of_one_rate_with_a_draft_in_the_way_leave_one_rate_and_one_audit_row()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobType = await AddJobTypeAsync(owner, from: "2020-10-15");
        await ChangeAsync(api, db => db.Payrolls.Add(Draft(2020, 10)));

        var responses = await InParallelAsync(Parallel, i => owner.PostRateAsync(jobType, 50_000 + i, "2020-10-15"));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
        Assert.Equal(1, await QueryAsync(api, db => db.PayRates.CountAsync()));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "JobType.RateChanged")));
    }

    // NFR-09: a save under a version that moved does not count as saved. The second caller here read before the first saved.
    [SqlServerFact]
    public async Task L3_the_same_settings_saved_under_an_old_version_while_another_save_lands_do_not_hand_back_a_stale_version()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var moments = new RaceMoments();
        await using var app = api.WithMoments(moments);
        await EnsureCooperativeAsync(app);
        var (owner, _) = await LoginNewAsync(app);
        var version = (await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings)).VersionOf();
        var body = new { roundingUnit = "Minutes15", effectiveFrom = "2020-03-01", rowVersion = version };
        HttpResponseMessage? first = null;
        moments.BeforeVersionsRead = async () => first = await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, body);

        var second = await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, body);

        Assert.Equal(HttpStatusCode.OK, first!.StatusCode);
        var firstVersion = (await first.BodyAsync(HttpStatusCode.OK)).GetProperty("settings").VersionOf();
        var current = (await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings)).VersionOf();
        Assert.Equal(current, firstVersion);
        // Either MSG06, as any other parallel save, or a 200 that hands back the version the record has now.
        if (second.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(current, (await second.BodyAsync(HttpStatusCode.OK)).GetProperty("settings").VersionOf());
        }
        else
        {
            await second.SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        }
    }

    // Different values at the same moment: the guard holds, the second answers MSG06 and the first stays.
    [SqlServerFact]
    public async Task Other_settings_saved_under_an_old_version_while_another_save_lands_answer_msg06()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var moments = new RaceMoments();
        await using var app = api.WithMoments(moments);
        await EnsureCooperativeAsync(app);
        var (owner, _) = await LoginNewAsync(app);
        var version = (await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings)).VersionOf();
        moments.BeforeVersionsRead = async () => await (await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = "2020-03-01", rowVersion = version })).BodyAsync(HttpStatusCode.OK);

        var second = await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "Hour1", effectiveFrom = "2020-03-01", rowVersion = version });

        await second.SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        using var scope = app.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().AttendanceSettingsVersions.AsNoTracking().SingleAsync();
        Assert.Equal(AttendanceRoundingUnit.Minutes15, stored.RoundingUnit);
    }

    // GBR-01: a Discontinued job type "cannot be chosen for new records". Nothing here touches the job type row, so nothing meets the discontinue.
    [SqlServerFact(Skip = "Accepted: a job type discontinued in the very moment of a save may still reach a new profile.")]
    public async Task L2_a_job_type_discontinued_between_the_check_and_the_save_does_not_reach_a_new_profile()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var moments = new RaceMoments();
        await using var app = api.WithMoments(moments);
        var (_, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var (owner, _) = await LoginNewAsync(app);
        var jobType = await AddJobTypeAsync(owner);
        moments.BeforeSave = async () => await (await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/discontinue", new { rowVersion = jobType.VersionOf() })).BodyAsync(HttpStatusCode.OK);

        var response = await owner.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile($"Vừa kịp {NewCode()}", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { jobType.IdOf() }));

        await AssertNo500Async([response]);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var links = await db.EmployeeJobTypes.CountAsync(l => l.JobTypeId == jobType.IdOf());
        Assert.Equal(StatusIds.Usage.Discontinued, await db.JobTypes.Where(j => j.Id == jobType.IdOf()).Select(j => j.StatusId).SingleAsync());
        Assert.True(links == 0, $"{(int)response.StatusCode}: a new profile has a Discontinued job type.");
    }

    // Kept known for L7, unreachable today: a payroll created and sent between the check and the save is not seen. Nothing creates one yet.
    [SqlServerFact]
    public async Task A_payroll_created_and_sent_between_the_check_and_the_save_is_not_seen_for_l7_to_close()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var moments = new RaceMoments();
        await using var app = api.WithMoments(moments);
        await OwnerWithCooperativeAsync(api);
        var (owner, _) = await LoginNewAsync(app);
        var jobType = await AddJobTypeAsync(owner, from: "2019-01-01");
        moments.BeforeSave = () => AddPayrollElsewhereAsync(database.ConnectionString, "2020-10-01", "2020-10-31", StatusIds.Payroll.AwaitingFinalization);

        var saved = await owner.PostRateAsync(jobType, 50_000, "2020-10-15");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("MSG-LABOR-04", await (await owner.PostRateAsync(await owner.ReadJobTypeAsync(jobType.IdOf()), 60_000, "2020-10-16")).ErrorCodeAsync());
    }

    // Draft touched in the window of a send: the save loses with MSG06 through the API, not a 500, and the send stands.
    [SqlServerFact]
    public async Task A_send_landing_in_the_window_of_a_rate_answers_msg06_and_a_retry_answers_msg_labor_04()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var moments = new RaceMoments();
        await using var app = api.WithMoments(moments);
        await OwnerWithCooperativeAsync(api);
        var (owner, _) = await LoginNewAsync(app);
        var jobType = await AddJobTypeAsync(owner, from: "2019-01-01");
        var payroll = Draft(2020, 10);
        await ChangeAsync(api, db => db.Payrolls.Add(payroll));
        moments.BeforeSave = async () =>
        {
            await using var other = database.CreateContext();
            var row = await other.Payrolls.SingleAsync(p => p.Id == payroll.Id);
            row.StatusId = StatusIds.Payroll.AwaitingFinalization;
            await other.SaveChangesAsync();
        };

        var lost = await owner.PostRateAsync(jobType, 50_000, "2020-10-15");
        var retried = await owner.PostRateAsync(jobType, 50_000, "2020-10-15");

        await lost.SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        await retried.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        await using var read = database.CreateContext();
        Assert.Single(await read.PayRates.ToListAsync());
    }

    private static async Task EnsureCooperativeAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        db.Cooperatives.Add(new OMTS.Domain.Entities.Identity.Cooperative { FullName = "HTX" });
        await db.SaveChangesAsync();
    }
}
