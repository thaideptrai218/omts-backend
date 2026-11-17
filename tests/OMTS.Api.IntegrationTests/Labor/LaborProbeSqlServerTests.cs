using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborProbeKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// The probes of <see cref="LaborProbeTests"/> that need a real SQL Server (real row versions, unique indexes, parallel saves): (b) the
/// Owner correction of a day and (d) employee requests. Same method: assert the SAFE behaviour, so a failure is a finding.
/// </summary>
public class LaborProbeSqlServerTests
{
    private static readonly DateOnly PastDay = new(2026, 3, 2);

    private sealed record ProbeSetup(SqlServerApiFactory Api, HttpClient Owner, FarmingZone Zone, Raft Raft, Guid Harvest);

    private static async Task<ProbeSetup> StartSetupAsync(SqlServerApiFactory api)
    {
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var raft = new Raft { ZoneId = zone.Id, Code = $"{zone.Code}-B01", Size = "1" };
        await ChangeAsync(api, db => db.Rafts.Add(raft));
        var harvest = (await AddJobTypeAsync(owner, $"Harvest {NewCode()}")).IdOf();
        return new ProbeSetup(api, owner, zone, raft, harvest);
    }

    private static async Task<Guid> NewEmployeeAsync(ProbeSetup s) =>
        IdOf(await CreateAsync(s.Owner, Profile($"Worker {NewCode()}", NewPhone(), s.Zone.Id)));

    /// <summary>The employee reads and sends their own work through an account linked to the profile.</summary>
    private static async Task<HttpClient> LinkedClientAsync(ProbeSetup s, Guid employeeId)
    {
        var password = NewPassword();
        var account = await s.Api.AddAccountAsync($"nv{NewCode()}", password);
        await ChangeAsync(s.Api, db => db.Employees.Single(e => e.Id == employeeId).AccountId = account.Id);
        var client = s.Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginTokensAsync(client, account.Username, password)).GetProperty("accessToken").GetString());
        return client;
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // (b) Owner correction of a day
    // ------------------------------------------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Probe_b1_sql_a_correction_with_a_version_read_before_another_correction_is_refused_with_msg06()
    {
        // FAILURE MEANS: with real SQL Server row versions, an Owner tab holding the version read before another correction overwrites that correction (lost update) instead of getting 409 MSG06.
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var employee = await NewEmployeeAsync(s);
        object[] periods = [PeriodOf(s.Harvest, s.Raft.Id, PastDay, null)];
        var created = await (await s.Owner.PutAsJsonAsync(DayUrl(employee, PastDay), Correction(PastDay, "06:00", "12:00", periods, null))).BodyAsync(HttpStatusCode.OK);
        var screen = created.GetProperty("day").Str("rowVersion");
        var other = await (await s.Owner.PutAsJsonAsync(DayUrl(employee, PastDay), Correction(PastDay, "06:00", "13:00", periods, screen))).BodyAsync(HttpStatusCode.OK);
        Assert.NotEqual(screen, other.GetProperty("day").Str("rowVersion"));

        var stale = await s.Owner.PutAsJsonAsync(DayUrl(employee, PastDay), Correction(PastDay, "06:00", "14:00", periods, screen));

        Assert.Equal(("MSG06", HttpStatusCode.Conflict), (await stale.ErrorCodeAsync(), stale.StatusCode));
        Assert.Equal(DateTimeOffset.Parse(Time(PastDay, "13:00")), (await QueryAsync(api, db => db.Attendances.SingleAsync(a => a.EmployeeId == employee))).CheckOutAt);
    }

    [SqlServerFact(Skip = "Finding of the review of 13/11 (OMTS-227): a correction of a day that got a record after the screen was read answers 400 MSG-GEN-01, not 409 MSG06. Remove the Skip when it is fixed.")]
    public async Task Probe_b1b_sql_a_correction_of_a_day_that_got_a_record_after_the_screen_was_read_is_refused_with_msg06()
    {
        // FAILURE MEANS: the Owner opened a day with no record (empty version), a check-in was recorded meanwhile, and the correction is answered 400 RowVersion (or overwrites the check-in) instead of 409 MSG06. Strict reading of NFR-09; the code treats an empty version of an existing record as invalid input.
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var employee = await NewEmployeeAsync(s);
        var today = Today();
        var rows = await (await AssignAsync(s.Owner, s.Raft.Id, s.Harvest, today, false, employee)).BodyAsync(HttpStatusCode.Created);
        var screen = await s.Owner.GetFromJsonAsync<JsonElement>(DayUrl(employee, today));
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("rowVersion").ValueKind);
        await (await CheckInAsync(s.Owner, RowId(rows, employee))).BodyAsync(HttpStatusCode.OK);
        object[] periods = [PeriodOf(s.Harvest, s.Raft.Id, today, null)];

        var stale = await s.Owner.PutAsJsonAsync(DayUrl(employee, today), Correction(today, "00:30", "01:00", periods, ""));

        Assert.Equal(("MSG06", HttpStatusCode.Conflict), (await stale.ErrorCodeAsync(), stale.StatusCode));
        Assert.Equal(AttendanceMethod.Manual, (await QueryAsync(api, db => db.Attendances.SingleAsync(a => a.EmployeeId == employee))).CheckInMethod);
    }

    [SqlServerFact]
    public async Task Probe_b1d_sql_parallel_corrections_with_the_same_version_leave_one_winner_and_a_clean_409()
    {
        // FAILURE MEANS: two Owner tabs saving at once from the same version both succeed (lost update), or the loser gets a 5xx / a code other than MSG06 instead of a clean 409.
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var employee = await NewEmployeeAsync(s);
        object[] periods = [PeriodOf(s.Harvest, s.Raft.Id, PastDay, null)];
        var created = await (await s.Owner.PutAsJsonAsync(DayUrl(employee, PastDay), Correction(PastDay, "06:00", "12:00", periods, null))).BodyAsync(HttpStatusCode.OK);
        var screen = created.GetProperty("day").Str("rowVersion");

        var responses = await InParallelAsync(6, i => s.Owner.PutAsJsonAsync(DayUrl(employee, PastDay), Correction(PastDay, "06:00", $"13:{i * 5:00}", periods, screen)));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
        Assert.Equal(2, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "Attendance.Corrected")));
    }

    [SqlServerFact]
    public async Task Probe_b2_sql_parallel_first_corrections_of_a_day_without_a_record_leave_one_record_and_a_clean_409()
    {
        // FAILURE MEANS: on a day with no attendance record, parallel first corrections leave two records or two first job periods, or a loser gets a 5xx or a 400 instead of a clean 409 (a late loser answered 400 RowVersion is the b1b finding showing up).
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var employee = await NewEmployeeAsync(s);
        object[] periods = [PeriodOf(s.Harvest, s.Raft.Id, PastDay, null)];
        var body = Correction(PastDay, "06:00", "16:00", periods, "");

        var responses = await InParallelAsync(6, _ => s.Owner.PutAsJsonAsync(DayUrl(employee, PastDay), body));

        await AssertNo500Async(responses);
        Assert.Equal(1, await QueryAsync(api, db => db.Attendances.CountAsync(a => a.EmployeeId == employee)));
        Assert.Equal(1, await QueryAsync(api, db => db.WorkAssignments.CountAsync(w => w.EmployeeId == employee)));
        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG-GEN-02", "MSG06");
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // (d) Employee requests
    // ------------------------------------------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Probe_d6_sql_a_cancel_racing_an_approval_leaves_one_winner()
    {
        // FAILURE MEANS: in some round both the cancel and the approval succeed, or neither does, or the stored status/cancel time/review time/audit disagree with the winner, or the loser gets a 5xx (LABOR-10.2.E1: reviewed in the meantime).
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var employee = await NewEmployeeAsync(s);
        var mine = await LinkedClientAsync(s, employee);

        for (var round = 0; round < 12; round++)
        {
            var day = Today().AddDays(3 + round * 2);
            var sent = await (await Submit(mine, Leave(day, day))).BodyAsync(HttpStatusCode.Created);
            var id = sent.GetProperty("id").GetGuid();

            var responses = await InParallelAsync(2, i => i == 0 ? mine.DeleteAsync($"{Me}/leave-requests/{id}") : Review(s.Owner, sent, true));

            await AssertNo500Async(responses);
            Assert.Equal((round, 1), (round, responses.Count(r => r.IsSuccessStatusCode)));
            var cancelWon = responses[0].IsSuccessStatusCode;
            Assert.Contains(await responses[cancelWon ? 1 : 0].ErrorCodeAsync(), new[] { "MSG-LABOR-26", "MSG06", "MSG-GEN-09" });
            var stored = await QueryAsync(api, db => db.EmployeeRequests.SingleAsync(r => r.Id == id));
            Assert.Equal((round, cancelWon ? StatusIds.EmployeeRequest.Cancelled : StatusIds.EmployeeRequest.Approved), (round, stored.StatusId));
            Assert.Equal((round, cancelWon, !cancelWon), (round, stored.CancelledAt is not null, stored.ReviewedAt is not null));
            Assert.Equal((round, 1), (round, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "EmployeeRequest.StatusChanged" && e.RecordId == id))));
        }
    }

    [SqlServerFact(Skip = "Finding of the review of 13/11 (OMTS-227): parallel leave requests for the same day leave several awaiting requests. Remove the Skip when it is fixed.")]
    public async Task Probe_d7_sql_parallel_leave_requests_for_the_same_day_leave_one_awaiting_request()
    {
        // FAILURE MEANS: the one-Awaiting-request-per-day-and-type rule (LABOR-10.0.E3) is only a read-then-insert check for leave (the filtered unique index covers missed times only), so parallel sends with different client operations save several requests.
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var employee = await NewEmployeeAsync(s);
        var mine = await LinkedClientAsync(s, employee);
        var day = Today().AddDays(5);

        var responses = await InParallelAsync(6, _ => Submit(mine, Leave(day, day)));

        await AssertNo500Async(responses);
        Assert.Equal(1, await QueryAsync(api, db => db.EmployeeRequests.CountAsync(r => r.EmployeeId == employee && r.StatusId == StatusIds.EmployeeRequest.AwaitingReview)));
        await AssertOneWinnerAsync(responses, HttpStatusCode.Created, "MSG-GEN-02");
    }
}
