using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
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
/// LABOR-12 to LABOR-14 on a real SQL Server: the migration with its back-fill of older headers, the sums and filters the in-memory provider only
/// pretends to run, and parallel requests that must meet a row version or a unique index and answer 409 or 400, never 500, and never pay twice.
/// </summary>
public class PayrollSqlServerTests
{
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    [SqlServerFact]
    public async Task The_migration_fills_the_older_headers_and_reverts_and_applies_again()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var own = all.Single(m => m.EndsWith("_PayrollSheetsPayslipsAndWagePayments", StringComparison.Ordinal));
        var previous = all[all.IndexOf(own) - 1];
        var migrator = db.GetService<IMigrator>();
        const string tables = "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN (N'PayrollLines', N'PayrollLineJobs', N'PayrollSelectedEmployees', N'WagePayments', N'PayrollAdvanceDeductions')";

        await migrator.MigrateAsync(previous);
        // The two headers the demo seed made before payroll sheets: the month before last Finalized and last month Awaiting finalization.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Payrolls (Id, PeriodStart, PeriodEnd, StatusId, CreatedBySystem, CreatedAt)
            VALUES (NEWID(), '2026-08-01', '2026-08-31', 3404, 0, SYSDATETIMEOFFSET()), (NEWID(), '2026-09-01', '2026-09-30', 3402, 0, SYSDATETIMEOFFSET()), (NEWID(), '2026-10-05', '2026-10-20', 3401, 0, SYSDATETIMEOFFSET())
            """);
        Assert.Equal(0, await ScalarAsync(db, tables));

        await migrator.MigrateAsync(own);
        Assert.Equal(5, await ScalarAsync(db, tables));
        var headers = await db.Payrolls.OrderBy(p => p.PeriodStart).Select(p => new { p.Code, Type = p.PeriodType.ToString(), Scope = p.Scope.ToString(), p.StatusId }).ToListAsync();
        Assert.Equal([("BL000001", "Monthly", "All", 3404), ("BL000002", "Monthly", "All", 3402), ("BL000003", "Custom", "All", 3401)], headers.Select(h => (h.Code, h.Type, h.Scope, h.StatusId)));
        Assert.Equal(3, await ScalarAsync(db, "SELECT CAST(LastValue AS int) AS [Value] FROM DocumentSequences WHERE [Key] = N'BL'"));
        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE Id IN (3405, 3406) AND [Group] = N'PAYROLL'"));
        Assert.Equal(4, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM StatusTransitions WHERE Id BETWEEN 233 AND 236"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_Payrolls_Code' AND is_unique = 1 AND has_filter = 1"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_WagePayments_RequestKey_RequestSequence' AND is_unique = 1 AND has_filter = 1"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID(N'Payrolls') AND is_unique = 1 AND is_primary_key = 0 AND name IN ('IX_Payrolls_PeriodStart', 'IX_Payrolls_PeriodEnd')"));
        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name IN ('CK_WagePayments_Amount', 'CK_WagePayments_Line')"));
        Assert.False(db.Database.HasPendingModelChanges());

        await migrator.MigrateAsync(previous);
        Assert.Equal(0, await ScalarAsync(db, tables));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM DocumentSequences WHERE [Key] IN (N'BL', N'PL')"));
        Assert.Equal(3, await db.Payrolls.CountAsync());

        await migrator.MigrateAsync(own);
        Assert.Equal(5, await ScalarAsync(db, tables));
        Assert.Equal(["BL000001", "BL000002", "BL000003"], await db.Payrolls.OrderBy(p => p.PeriodStart).Select(p => p.Code).ToListAsync());
    }

    private sealed record Setup(SqlServerApiFactory Api, HttpClient Owner, FarmingZone Zone, Raft Raft, Guid Harvest);

    private static async Task<Setup> StartSetupAsync(SqlServerApiFactory api)
    {
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var raft = new Raft { ZoneId = zone.Id, Code = $"{zone.Code}-B01", Size = "1" };
        await ChangeAsync(api, db => db.Rafts.Add(raft));
        var harvest = (await AddJobTypeAsync(owner, $"Harvest {NewCode()}")).IdOf();
        return new Setup(api, owner, zone, raft, harvest);
    }

    /// <summary>An employee with a complete day of 8 hours (07:00 to 15:00) on each of the days; 320,000 a day at the 40,000 of the test job type.</summary>
    private static async Task<Guid> WorkerAsync(Setup s, string name, params DateOnly[] days)
    {
        var employee = IdOf(await CreateAsync(s.Owner, Profile(name, NewPhone(), s.Zone.Id)));
        await ChangeAsync(s.Api, db =>
        {
            var account = db.Accounts.Select(a => a.Id).First();
            foreach (var day in days)
            {
                db.WorkAssignments.Add(new WorkAssignment { EmployeeId = employee, Date = day, JobTypeId = s.Harvest, RaftId = s.Raft.Id, AssignedByAccountId = account });
                db.Attendances.Add(new Attendance
                {
                    EmployeeId = employee, Date = day,
                    CheckInAt = DateTimeOffset.Parse(Time(day, "07:00")), CheckInMethod = AttendanceMethod.Manual,
                    CheckOutAt = DateTimeOffset.Parse(Time(day, "15:00")), CheckOutMethod = AttendanceMethod.Manual,
                });
            }
        });
        return employee;
    }

    private static object Month(int month, Guid employee) => new
    {
        periodType = "Monthly", from = $"2024-{month:00}-01", to = $"2024-{month:00}-{DateTime.DaysInMonth(2024, month):00}", scope = "Selected", employeeIds = new[] { employee },
    };

    [SqlServerFact]
    public async Task The_whole_payroll_runs_on_sql_server_sheet_finalize_pay_advance_and_lists()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var an = await WorkerAsync(s, "An", new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 2), new DateOnly(2024, 4, 1));

        var march = await (await s.Owner.PostAsJsonAsync("/api/payrolls", Month(3, an))).BodyAsync(HttpStatusCode.Created);
        var april = await (await s.Owner.PostAsJsonAsync("/api/payrolls", Month(4, an))).BodyAsync(HttpStatusCode.Created);
        var finalized = await (await s.Owner.PostAsJsonAsync($"/api/payrolls/{march.GetProperty("id").GetGuid()}/finalize", new { expenseMonth = "2024-03" })).BodyAsync(HttpStatusCode.OK);
        var slip = finalized.GetProperty("slips")[0].GetProperty("id").GetGuid();
        await (await s.Owner.PostAsJsonAsync($"/api/payrolls/{finalized.GetProperty("id").GetGuid()}/payments", new
        {
            paidAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"), method = "BankTransfer", note = "", lines = new[] { new { slipId = slip, amount = 640_000 } },
        })).BodyAsync(HttpStatusCode.Created);
        await (await s.Owner.PostAsJsonAsync("/api/payroll-payments", new { employeeId = an, amount = 100_000, paidAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"), method = "Cash", note = "" })).BodyAsync(HttpStatusCode.Created);

        var list = await s.Owner.GetFromJsonAsync<JsonElement>("/api/payrolls?sortBy=total&sortDir=desc");
        Assert.Equal((2, 960_000m, 640_000m, 320_000m), (list.GetProperty("total").GetInt32(), list.GetProperty("summary").GetProperty("total").GetDecimal(), list.GetProperty("summary").GetProperty("paid").GetDecimal(), list.GetProperty("summary").GetProperty("outstanding").GetDecimal()));
        var payslips = await s.Owner.GetFromJsonAsync<JsonElement>($"/api/employees/{an}/payslips?status=Finalized");
        Assert.Equal((0m, 100_000m), (payslips.GetProperty("summary").GetProperty("outstanding").GetDecimal(), payslips.GetProperty("summary").GetProperty("advanceBalance").GetDecimal()));
        var payable = await s.Owner.GetFromJsonAsync<JsonElement>("/api/payrolls/payable-employees?includeAll=true");
        Assert.Equal(0m, payable.GetProperty("items")[0].GetProperty("outstanding").GetDecimal());
        var payments = await s.Owner.GetFromJsonAsync<JsonElement>($"/api/payroll-payments?employeeId={an}");
        Assert.Equal(["Advance", "Wage"], payments.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()!));
        var cash = await s.Owner.GetFromJsonAsync<JsonElement>("/api/cash-book?fund=Bank&search=CNH&pageSize=50");
        Assert.Equal(1, cash.GetProperty("items").GetArrayLength());
        var detail = await s.Owner.GetFromJsonAsync<JsonElement>($"/api/payroll-slips/{slip}");
        Assert.Equal(2, detail.GetProperty("attendance").GetArrayLength());

        var finalizedApril = await (await s.Owner.PostAsJsonAsync($"/api/payrolls/{april.GetProperty("id").GetGuid()}/finalize", new { expenseMonth = "2024-04" })).BodyAsync(HttpStatusCode.OK);
        Assert.Equal((320_000m, 100_000m, 220_000m), (finalizedApril.GetProperty("slips")[0].GetProperty("income").GetDecimal(), finalizedApril.GetProperty("slips")[0].GetProperty("deduction").GetDecimal(), finalizedApril.GetProperty("slips")[0].GetProperty("net").GetDecimal()));
        var export = await s.Owner.GetAsync("/api/payrolls/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    [SqlServerFact]
    public async Task Parallel_payments_finalizations_and_sheets_do_one_thing_once_and_the_others_get_409_or_400()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var s = await StartSetupAsync(api);
        var an = await WorkerAsync(s, "An", new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 2));
        var bao = await WorkerAsync(s, "Bảo", new DateOnly(2024, 3, 1));

        // The same sheet made six times at once: one is saved.
        var made = await InParallelAsync(6, _ => s.Owner.PostAsJsonAsync("/api/payrolls", Month(3, an)));
        await AssertNo500Async(made);
        Assert.Single(made, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(1, await QueryAsync(api, db => db.Payrolls.CountAsync()));
        var sheet = await QueryAsync(api, db => db.Payrolls.Include(p => p.Lines).SingleAsync());

        // Finalized six times at once: one wins.
        var finalizing = await InParallelAsync(6, _ => s.Owner.PostAsJsonAsync($"/api/payrolls/{sheet.Id}/finalize", new { expenseMonth = "2024-03" }));
        await AssertOneWinnerAsync(finalizing, HttpStatusCode.OK, "MSG06", "MSG-GEN-09");

        // The whole of one payslip paid six times at once: one voucher, and what is paid is never more than what is owed.
        var slip = sheet.Lines.Single().Id;
        var paying = await InParallelAsync(6, _ => s.Owner.PostAsJsonAsync($"/api/payrolls/{sheet.Id}/payments", new
        {
            paidAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"), method = "Cash", note = "", lines = new[] { new { slipId = slip, amount = 640_000 } },
        }));
        foreach (var response in paying)
        {
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict or HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        Assert.Single(paying, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(640_000m, await QueryAsync(api, db => db.WagePayments.Where(w => w.PayrollLineId == slip).SumAsync(w => w.Amount)));
        Assert.Equal(1, await QueryAsync(api, db => db.CashVouchers.CountAsync()));

        // The same request key six times at once: one payment, the others the same answer or 409.
        var keyed = await InParallelAsync(6, _ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/payroll-payments")
            {
                Content = JsonContent.Create(new { employeeId = bao, amount = 50_000, paidAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"), method = "Cash", note = "" }),
                Headers = { { "Idempotency-Key", "advance-0001" } },
            };
            return s.Owner.SendAsync(request);
        });
        await AssertNo500Async(keyed);
        Assert.Equal(1, await QueryAsync(api, db => db.WagePayments.CountAsync(w => w.EmployeeId == bao)));
    }
}
