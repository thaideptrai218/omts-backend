using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LABOR-02, LABOR-04, LABOR-05 and LABOR-10 on a real SQL Server: the migration, the queries the in-memory provider only pretends to run
/// (ordering, filtered indexes, includes), and parallel recordings that must meet a unique index and answer 409, never 500.
/// </summary>
public class WorkSqlServerTests
{
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    [SqlServerFact]
    public async Task The_migration_adds_the_work_tables_and_the_request_statuses_and_reverts_and_applies_again()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var own = all.Single(m => m.EndsWith("_WorkAssignmentsAttendanceAndEmployeeRequests", StringComparison.Ordinal));
        var previous = all[all.IndexOf(own) - 1];
        var migrator = db.GetService<IMigrator>();
        const string tables = "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN (N'WorkAssignments', N'Attendances', N'EmployeeRequests')";
        const string statuses = "SELECT COUNT(*) AS [Value] FROM Statuses WHERE [Group] = N'EMPLOYEE_REQUEST' AND Id BETWEEN 3801 AND 3804";
        const string transitions = "SELECT COUNT(*) AS [Value] FROM StatusTransitions WHERE Id IN (230, 231, 232) AND FromStatusId = 3801";

        await migrator.MigrateAsync(previous);
        Assert.Equal((0, 0, 0), (await ScalarAsync(db, tables), await ScalarAsync(db, statuses), await ScalarAsync(db, transitions)));

        await migrator.MigrateAsync(own);
        Assert.Equal((3, 4, 3), (await ScalarAsync(db, tables), await ScalarAsync(db, statuses), await ScalarAsync(db, transitions)));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'UX_WorkAssignments_Employee_Date_First' AND is_unique = 1 AND has_filter = 1"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'UX_EmployeeRequests_Awaiting_MissedTime' AND is_unique = 1 AND has_filter = 1"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'UX_EmployeeRequests_Employee_ClientOperation' AND is_unique = 1 AND has_filter = 1"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID(N'Attendances') AND is_unique = 1 AND is_primary_key = 0 AND has_filter = 0"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_EmployeeRequests_Period'"));
        Assert.False(db.Database.HasPendingModelChanges());

        await migrator.MigrateAsync(previous);
        Assert.Equal((0, 0, 0), (await ScalarAsync(db, tables), await ScalarAsync(db, statuses), await ScalarAsync(db, transitions)));

        await migrator.MigrateAsync(own);
        Assert.Equal((3, 4, 3), (await ScalarAsync(db, tables), await ScalarAsync(db, statuses), await ScalarAsync(db, transitions)));
    }

    /// <summary>The Owner account does everything here; an employee gets their own Owner-type login only to read and send their own work.</summary>
    private sealed record Setup(SqlServerApiFactory Api, HttpClient Owner, FarmingZone Zone, Raft Raft, Raft Raft2, Guid Harvest, Guid Hanging);

    private static async Task<Setup> StartSetupAsync(SqlServerApiFactory api)
    {
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var rafts = new[] { new Raft { ZoneId = zone.Id, Code = $"{zone.Code}-B01", Size = "1" }, new Raft { ZoneId = zone.Id, Code = $"{zone.Code}-B02", Size = "1" } };
        await ChangeAsync(api, db => db.Rafts.AddRange(rafts));
        var harvest = (await AddJobTypeAsync(owner, $"Harvest {NewCode()}")).IdOf();
        var hanging = (await AddJobTypeAsync(owner, $"Hanging {NewCode()}")).IdOf();
        return new Setup(api, owner, zone, rafts[0], rafts[1], harvest, hanging);
    }

    private static async Task<Guid> NewEmployeeAsync(Setup s, string? name = null) =>
        IdOf(await CreateAsync(s.Owner, Profile(name ?? $"Worker {NewCode()}", NewPhone(), s.Zone.Id)));

    private static async Task<HttpClient> LinkedClientAsync(Setup s, Guid employeeId)
    {
        var account = await s.Api.AddAccountAsync($"nv{NewCode()}", Password);
        await ChangeAsync(s.Api, db => db.Employees.Single(e => e.Id == employeeId).AccountId = account.Id);
        var client = s.Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginTokensAsync(client, account.Username)).GetProperty("accessToken").GetString());
        return client;
    }

    [SqlServerFact]
    public async Task The_whole_flow_runs_on_sql_server_assign_check_in_out_correct_request_review_and_my_work()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var (a, b) = (await NewEmployeeAsync(s, "An"), await NewEmployeeAsync(s, "Bình"));

        var rows = await (await AssignAsync(s.Owner, s.Raft.Id, s.Harvest, Today(), false, a, b)).BodyAsync(HttpStatusCode.Created);
        var rowA = RowId(rows, a);
        await (await CheckInAsync(s.Owner, rowA)).BodyAsync(HttpStatusCode.OK);
        var crew = await s.Owner.PostAsJsonAsync($"{Workforce}/crew-job", new { raftId = s.Raft2.Id, jobTypeId = s.Hanging, employeeIds = new[] { a, b } });
        Assert.Equal(HttpStatusCode.NoContent, crew.StatusCode);
        var checkedOut = await (await CheckOutAsync(s.Owner, rowA)).BodyAsync(HttpStatusCode.OK);
        Assert.True(checkedOut.GetProperty("complete").GetBoolean());
        var board = await s.Owner.GetFromJsonAsync<JsonElement>($"{Workforce}/assignments?day={Day(Today())}");
        Assert.Equal([s.Raft2.Code, s.Raft2.Code], board.EnumerateArray().Select(r => r.Str("raftCode")));
        var lookups = await s.Owner.GetFromJsonAsync<JsonElement>($"{Workforce}/lookups?day={Day(Today())}");
        Assert.Equal(2, lookups.GetProperty("employees").GetArrayLength());

        var day = new DateOnly(2026, 3, 2);
        var corrected = await s.Owner.PutAsJsonAsync($"{Workforce}/attendance/{b}/{Day(day)}", new
        {
            checkInAt = Time(day, "06:10"), checkOutAt = Time(day, "16:55"), reason = "Quên chấm công",
            periods = new object[] { new { jobTypeId = s.Hanging, raftId = s.Raft.Id, startsAt = (string?)null }, new { jobTypeId = s.Harvest, raftId = s.Raft.Id, startsAt = Time(day, "09:40") } },
        });
        Assert.Equal(2, (await corrected.BodyAsync(HttpStatusCode.OK)).GetProperty("day").GetProperty("jobs").GetArrayLength());

        var mine = await LinkedClientAsync(s, a);
        var first = await (await mine.PostAsJsonAsync($"{Me}/leave-requests", new { from = Day(Today().AddDays(3)), to = Day(Today().AddDays(3)), reason = "Việc nhà", clientOperationId = Guid.NewGuid() })).BodyAsync(HttpStatusCode.Created);
        var second = await (await mine.PostAsJsonAsync($"{Me}/leave-requests", new { type = "MissedCheckIn", day = Day(Today().AddDays(-1)), requestedTime = "06:30", reason = "Quên", clientOperationId = Guid.NewGuid() })).BodyAsync(HttpStatusCode.Created);
        var third = await (await mine.PostAsJsonAsync($"{Me}/leave-requests", new { from = Day(Today().AddDays(9)), to = Day(Today().AddDays(9)), reason = "Đám cưới", clientOperationId = Guid.NewGuid() })).BodyAsync(HttpStatusCode.Created);
        await (await s.Owner.PostAsJsonAsync($"{Workforce}/leave-requests/{second.GetProperty("id").GetGuid()}/review", new { approved = false, response = "Không đúng", rowVersion = second.Str("rowVersion") })).BodyAsync(HttpStatusCode.OK);

        var list = (await s.Owner.GetFromJsonAsync<JsonElement>($"{Workforce}/leave-requests")).EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([first.GetProperty("id").GetGuid(), third.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid()], list);
        var work = await mine.GetFromJsonAsync<JsonElement>($"{Me}/workforce?from={Day(Today().AddDays(-2))}&to={Day(Today())}");
        Assert.Equal(a, work.GetProperty("employeeId").GetGuid());
        Assert.Equal(1, work.GetProperty("attendance").GetArrayLength());
        Assert.Equal(3, work.GetProperty("requests").GetArrayLength());
        var qr = await mine.GetFromJsonAsync<JsonElement>($"{Me}/attendance-qr");
        Assert.Matches("^OMTS-ATT\\.", qr.Str("code"));
        var cancelled = await mine.DeleteAsync($"{Me}/leave-requests/{first.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
    }

    [SqlServerFact]
    public async Task Parallel_recordings_of_the_same_thing_leave_one_row_and_the_others_get_409()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var employee = await NewEmployeeAsync(s);

        // Assigning one employee on the same day from several callers: one first job period.
        var assigns = await InParallelAsync(6, _ => AssignAsync(s.Owner, s.Raft.Id, s.Harvest, Today(), false, employee));
        await AssertNo500Async(assigns);
        Assert.Equal(1, await QueryAsync(api, db => db.WorkAssignments.CountAsync(w => w.EmployeeId == employee)));
        var row = await RowOfAsync(s.Owner, employee);

        // One check-in per day.
        var checkIns = await InParallelAsync(6, _ => CheckInAsync(s.Owner, row.GetProperty("id").GetGuid()));
        await AssertOneWinnerAsync(checkIns, HttpStatusCode.OK, "MSG-GEN-02", "MSG-LABOR-14", "MSG06");
        Assert.Equal(1, await QueryAsync(api, db => db.Attendances.CountAsync(x => x.EmployeeId == employee)));

        // One request awaiting review per employee, day and missed time, and one saved request per client operation.
        var mine = await LinkedClientAsync(s, employee);
        var missed = new { type = "MissedCheckOut", day = Day(Today().AddDays(-1)), requestedTime = "16:00", reason = "Quên", clientOperationId = Guid.NewGuid() };
        var requests = await InParallelAsync(6, _ => mine.PostAsJsonAsync($"{Me}/leave-requests", missed));
        await AssertNo500Async(requests);
        Assert.Equal(1, await QueryAsync(api, db => db.EmployeeRequests.CountAsync(r => r.EmployeeId == employee)));
        var otherDay = new { type = "MissedCheckOut", day = Day(Today().AddDays(-2)), requestedTime = "16:00", reason = "Quên", clientOperationId = Guid.NewGuid() };
        var sameDay = await InParallelAsync(6, i => mine.PostAsJsonAsync($"{Me}/leave-requests", i % 2 == 0 ? otherDay : otherDay with { clientOperationId = Guid.NewGuid() }));
        await AssertNo500Async(sameDay);
        Assert.Equal(2, await QueryAsync(api, db => db.EmployeeRequests.CountAsync(r => r.EmployeeId == employee)));
    }
}
