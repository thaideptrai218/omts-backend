using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB rules that only a real SQL Server shows: the migration on old data, unique indexes and row versions under parallel requests.</summary>
public class LaborSqlServerTests
{
    // Migrations are regenerated on every rebase, so the one before the labor migration is looked up, not named.
    private static string MigrationBeforeLabor(DbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        return all[all.FindIndex(m => m.EndsWith("_AddWorkforceAndPayroll", StringComparison.Ordinal)) - 1];
    }

    [SqlServerFact]
    public async Task Migration_keeps_workers_and_daily_wage_assignments_and_refuses_old_attendance_clearly()
    {
        await using var kept = SqlServerTestDatabase.Create();
        await using (var db = kept.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeLabor(db));
            var (zone, worker, team, assignment) = await SeedOldLaborAsync(db);
            await db.Database.MigrateAsync();

            var migrated = await db.WorkAssignments.AsNoTracking().SingleAsync(a => a.Id == assignment);
            Assert.Equal((WorkPaymentType.DailyWage, worker, zone), (migrated.PaymentType, migrated.WorkerId!.Value, migrated.ZoneId));
            Assert.Empty(migrated.CompletionEvidenceUrls);
            Assert.Null((await db.SeasonalWorkers.AsNoTracking().SingleAsync(w => w.Id == worker)).ContractorId);
            Assert.True(await db.Contractors.AnyAsync(t => t.Id == team));
        }

        await using var refused = SqlServerTestDatabase.Create();
        await using (var db = refused.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeLabor(db));
            var (_, worker, _, _) = await SeedOldLaborAsync(db);
            var account = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt) VALUES ({account}, N'old', N'Old', N'x', N'Staff', 301, 0, 0, SYSDATETIMEOFFSET())");
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO AttendanceRecords (Id, WorkerId, WorkDate, Result, RecordedByAccountId, EvidenceUrls, CreatedAt) VALUES ({Guid.NewGuid()}, {worker}, '2026-10-01', N'FullDay', {account}, N'[]', SYSDATETIMEOFFSET())");

            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync());

            Assert.Contains("AddWorkforceAndPayroll", error.Message);
            Assert.Contains(MigrationBeforeLabor(db), await db.Database.GetAppliedMigrationsAsync());
        }
    }

    [SqlServerFact]
    public async Task Parallel_payments_of_one_payslip_never_overpay_it()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var worker = new SeasonalWorker { FullName = "Lý Văn Sáu", PhoneNumber = NewPhone() };
        var month = new DateOnly(2026, 8, 1);
        await ChangeAsync(api, db =>
        {
            db.SeasonalWorkers.Add(worker);
            db.PayRuleVersions.Add(new PayRuleVersion
            {
                WorkerId = worker.Id, SalaryType = SalaryType.DailyWage, Amount = 300_000, ValidFrom = month, ValidTo = month.AddMonths(1).AddDays(-1),
                EnteredByAccountId = account.Id, StatusId = StatusIds.PayRule.Approved,
            });
            db.AttendanceRecords.Add(new AttendanceRecord { ZoneId = zone.Id, WorkerId = worker.Id, WorkDate = month, Result = AttendanceResult.Present, RecordedByAccountId = account.Id });
        });
        var payroll = await Body(await owner.PostAsJsonAsync("/api/payrolls", new { period = "2026-08" }), HttpStatusCode.Created);
        await Body(await owner.PostAsync("/api/payrolls/2026-08/finalize", null), HttpStatusCode.OK);
        var payslip = payroll.GetProperty("payslips")[0].GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => owner.PostAsJsonAsync(
            $"/api/payslips/{payslip}/payments", new { amountPaid = 300_000, paymentDate = "2026-09-01", method = "Cash", referenceCode = $"R{i}" })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        await using var check = database.CreateContext();
        Assert.Equal(300_000m, await check.LaborPayments.Where(p => p.SettlementId == payslip).SumAsync(p => p.AmountPaid));
        Assert.Equal(StatusIds.LaborSettlement.Paid, await check.LaborSettlements.Where(s => s.Id == payslip).Select(s => s.StatusId).SingleAsync());
    }

    [SqlServerFact]
    public async Task Parallel_creations_of_one_month_and_one_person_attendance_keep_one_record_and_answer_MSG03()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var first = await AddZoneAsync(api, cooperative.Id);
        var second = await AddZoneAsync(api, cooperative.Id);
        var worker = new SeasonalWorker { FullName = "Mai Văn Bảy", PhoneNumber = NewPhone() };
        await ChangeAsync(api, db => db.SeasonalWorkers.Add(worker));

        var payrolls = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => owner.PostAsJsonAsync("/api/payrolls", new { period = "2026-07" })));
        var sheets = await Task.WhenAll(new[] { first, second, first, second }.Select(zone => owner.PutAsJsonAsync(
            $"/api/zones/{zone.Id}/attendance/2026-10-01", new { rows = new[] { new { workerId = worker.Id, result = "Present" } } })));

        Assert.Single(payrolls, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in payrolls.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal("MSG03", await refused.ErrorCodeAsync());
        }

        Assert.Contains(sheets, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var refused in sheets.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.True(await refused.ErrorCodeAsync() is "MSG03" or "MSG06", await refused.Content.ReadAsStringAsync());
        }

        await using var check = database.CreateContext();
        Assert.Equal(1, await check.Payrolls.CountAsync(p => p.Period == "2026-07"));
        Assert.Equal(1, await check.AttendanceRecords.CountAsync(r => r.WorkerId == worker.Id));
    }

    [SqlServerFact]
    public async Task Editing_a_worker_from_an_old_version_is_MSG06_with_the_last_editor()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, _) = await OwnerWithCooperativeAsync(api);
        var created = await Body(await owner.PostAsJsonAsync("/api/labor/workers", new { fullName = "Tạ Văn Chín", phoneNumber = NewPhone() }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var oldVersion = created.GetProperty("rowVersion").GetString();
        var phone = created.GetProperty("phoneNumber").GetString();

        await Body(await owner.PutAsJsonAsync($"/api/labor/workers/{id}", new { fullName = "Tạ Văn Chín", phoneNumber = phone, citizenId = "123456789", rowVersion = oldVersion }), HttpStatusCode.OK);
        var stale = await owner.PutAsJsonAsync($"/api/labor/workers/{id}", new { fullName = "Tạ Văn Mười", phoneNumber = phone, rowVersion = oldVersion });

        var problem = await Body(stale, HttpStatusCode.Conflict);
        Assert.Equal("MSG06", problem.GetProperty("code").GetString());
        Assert.Contains(account.DisplayName, problem.GetProperty("detail").GetString());
    }

    private static async Task<(Guid Zone, Guid Worker, Guid Team, Guid Assignment)> SeedOldLaborAsync(DbContext db)
    {
        var (cooperative, zone, worker, team, assignment) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, StatusId, CreatedAt) VALUES ({cooperative}, N'HTX', 201, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync($"INSERT INTO FarmingZones (Id, CooperativeId, Code, Name, StatusId, CreatedAt) VALUES ({zone}, {cooperative}, N'Z1', N'Zone 1', 101, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync($"INSERT INTO Contractors (Id, Name, StatusId, CreatedAt) VALUES ({team}, N'Tổ cũ', 101, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync($"INSERT INTO SeasonalWorkers (Id, FullName, PhoneNumber, StatusId, CreatedAt) VALUES ({worker}, N'Người cũ', N'0912000111', 101, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO WorkAssignments (Id, ZoneId, WorkerId, Task, WorkDate, PaymentType, AcceptanceEvidenceUrls, CreatedAt) VALUES ({assignment}, {zone}, {worker}, N'Trực bè', '2026-10-01', N'DailyWage', N'[]', SYSDATETIMEOFFSET())");
        return (zone, worker, team, assignment);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }
}
