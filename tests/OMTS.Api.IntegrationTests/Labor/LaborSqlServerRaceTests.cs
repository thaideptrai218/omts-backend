using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB rules only a real SQL Server shows: unique indexes under parallel saves (workers, teams, payments, pay rates), row
/// versions when finalize, refresh, manual lines, reopen and payments race, check constraints on the labor tables, and a
/// migration refused half-way leaving no half-applied schema.
/// </summary>
public class LaborSqlServerRaceTests
{
    private static readonly DateOnly Month = new(2026, 8, 1);
    private static readonly DateOnly MonthEnd = new(2026, 8, 31);

    private static async Task<JsonElement> Body(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>A worker with a daily rate of 300,000 in 08/2026 and the given paid days in a new zone.</summary>
    private static async Task<SeasonalWorker> PaidWorkerAsync(SqlServerApiFactory api, Guid accountId, FarmingZone zone, params int[] days)
    {
        var worker = new SeasonalWorker { FullName = $"Đinh Văn {NewCode()}", PhoneNumber = NewPhone() };
        await ChangeAsync(api, db =>
        {
            db.SeasonalWorkers.Add(worker);
            db.PayRuleVersions.Add(new PayRuleVersion
            {
                WorkerId = worker.Id, SalaryType = SalaryType.DailyWage, Amount = 300_000, ValidFrom = Month, ValidTo = MonthEnd,
                EnteredByAccountId = accountId, StatusId = StatusIds.PayRule.Approved,
            });
            foreach (var day in days)
            {
                db.AttendanceRecords.Add(new AttendanceRecord
                {
                    ZoneId = zone.Id, WorkerId = worker.Id, WorkDate = Month.AddDays(day - 1), Result = AttendanceResult.Present, RecordedByAccountId = accountId,
                });
            }
        });
        return worker;
    }

    private static async Task<IReadOnlyList<HttpResponseMessage>> InParallelAsync(params Func<Task<HttpResponseMessage>>[] requests) =>
        await Task.WhenAll(requests.Select(request => Task.Run(request)));

    [SqlServerFact]
    public async Task Parallel_saves_of_one_worker_or_one_team_name_keep_one_record_and_answer_MSG03_with_its_id()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var phone = NewPhone();
        var citizenId = $"0792{Random.Shared.Next(10_000_000, 99_999_999)}";

        var sameNameAndPhone = await InParallelAsync(Enumerable.Range(0, 4).Select(i => (Func<Task<HttpResponseMessage>>)(() => owner.PostAsJsonAsync("/api/labor/workers",
            new { fullName = i % 2 == 0 ? "Phan Văn Tư" : "PHAN VĂN TƯ", phoneNumber = i < 2 ? phone : "+84" + phone[1..] }))).ToArray());
        var sameCitizenId = await InParallelAsync(Enumerable.Range(0, 3).Select(i => (Func<Task<HttpResponseMessage>>)(() => owner.PostAsJsonAsync("/api/labor/workers",
            new { fullName = $"Người số {i}", citizenId }))).ToArray());
        var sameTeamName = await InParallelAsync(new[] { "Tổ Đông Hải", "TỔ ĐÔNG HẢI", "tổ đông hải" }
            .Select(name => (Func<Task<HttpResponseMessage>>)(() => owner.PostAsJsonAsync("/api/labor/teams", new { name }))).ToArray());

        foreach (var group in new[] { sameNameAndPhone, sameCitizenId, sameTeamName })
        {
            var winner = await Body(Assert.Single(group, r => r.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created);
            foreach (var refused in group.Where(r => r.StatusCode != HttpStatusCode.Created))
            {
                var problem = await Body(refused, HttpStatusCode.Conflict);
                Assert.Equal("MSG03", problem.GetProperty("code").GetString());
                Assert.Equal(winner.GetProperty("id").GetGuid(), problem.GetProperty("existingId").GetGuid());
            }
        }

        await using var check = database.CreateContext();
        Assert.Equal(1, await check.SeasonalWorkers.CountAsync(w => w.PhoneNumber == phone));
        Assert.Equal(1, await check.SeasonalWorkers.CountAsync(w => w.CitizenId == citizenId));
        Assert.Equal(1, await check.Contractors.CountAsync(t => t.Name == "Tổ Đông Hải"));
    }

    [SqlServerFact]
    public async Task Parallel_identical_payments_of_one_payslip_record_one_payment()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        await PaidWorkerAsync(api, account.Id, zone, 1, 2);
        var payroll = await Body(await owner.PostAsJsonAsync("/api/payrolls", new { period = "2026-08" }), HttpStatusCode.Created);
        await Body(await owner.PostAsync("/api/payrolls/2026-08/finalize", null), HttpStatusCode.OK);
        var payslip = payroll.GetProperty("payslips")[0].GetProperty("id").GetGuid();

        var responses = await InParallelAsync(Enumerable.Range(0, 4).Select(_ => (Func<Task<HttpResponseMessage>>)(() => owner.PostAsJsonAsync(
            $"/api/payslips/{payslip}/payments", new { amountPaid = 100_000, paymentDate = "2026-09-01", method = "Cash", referenceCode = "PC-0901" })))
            .ToArray());

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.True(await refused.ErrorCodeAsync() is "MSG03" or "MSG06", await refused.Content.ReadAsStringAsync());
        }

        await using var check = database.CreateContext();
        Assert.Equal(1, await check.LaborPayments.CountAsync(p => p.SettlementId == payslip));
    }

    [SqlServerFact]
    public async Task Finalize_racing_with_refresh_a_manual_line_and_a_second_finalize_leaves_a_consistent_payroll()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var first = await PaidWorkerAsync(api, account.Id, zone, 1, 2);
        await PaidWorkerAsync(api, account.Id, zone, 3);
        var payroll = await Body(await owner.PostAsJsonAsync("/api/payrolls", new { period = "2026-08" }), HttpStatusCode.Created);
        var firstSlip = payroll.GetProperty("payslips").EnumerateArray().Single(p => p.GetProperty("payee").GetProperty("id").GetGuid() == first.Id).GetProperty("id").GetGuid();

        var responses = await InParallelAsync(
            () => owner.PostAsync("/api/payrolls/2026-08/finalize", null),
            () => owner.PostAsync("/api/payrolls/2026-08/finalize", null),
            () => owner.PostAsync("/api/payrolls/2026-08/refresh", null),
            () => owner.PostAsJsonAsync($"/api/payrolls/2026-08/payslips/{firstSlip}/lines", new { type = "Addition", amount = 50_000, reason = "Thưởng" }));

        Assert.True(responses.Take(2).Count(r => r.StatusCode == HttpStatusCode.OK) <= 1);
        foreach (var refused in responses.Where(r => (int)r.StatusCode >= 400))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.True(await refused.ErrorCodeAsync() is "MSG06" or "MSG61" or "PERIOD_LOCKED", await refused.Content.ReadAsStringAsync());
        }

        await using (var check = database.CreateContext())
        {
            var saved = await check.Payrolls.AsNoTracking().Include(p => p.Settlements).ThenInclude(s => s.PayLines).SingleAsync(p => p.Period == "2026-08");
            var expected = saved.FinalizedAt is null ? StatusIds.LaborSettlement.Draft : StatusIds.LaborSettlement.Approved;
            Assert.Equal(2, saved.Settlements.Count);
            foreach (var settlement in saved.Settlements)
            {
                Assert.Equal(expected, settlement.StatusId);
                Assert.Equal(PayslipBalance.Of(settlement.PayLines.Select(l => (l.LineType, l.Amount))).Net, settlement.TotalObligation);
                Assert.Equal(saved.FinalizedAt is null ? 0 : 1,
                    await check.AuditEvents.CountAsync(e => e.RecordId == settlement.Id && e.Action == "LaborSettlement.StatusChanged"));
            }

            Assert.True(saved.Settlements.Sum(s => s.PayLines.Count(l => l.IsManual)) <= 1);
        }

        // Whatever lost the race, the month can still be finalized afterwards.
        var final = await owner.PostAsync("/api/payrolls/2026-08/finalize", null);
        Assert.True(final.StatusCode == HttpStatusCode.OK || await final.ErrorCodeAsync() == "MSG61", await final.Content.ReadAsStringAsync());
    }

    [SqlServerFact]
    public async Task Reopen_racing_with_payments_never_leaves_a_draft_payslip_with_payments()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        await PaidWorkerAsync(api, account.Id, zone, 1);
        var payroll = await Body(await owner.PostAsJsonAsync("/api/payrolls", new { period = "2026-08" }), HttpStatusCode.Created);
        await Body(await owner.PostAsync("/api/payrolls/2026-08/finalize", null), HttpStatusCode.OK);
        var payslip = payroll.GetProperty("payslips")[0].GetProperty("id").GetGuid();

        var responses = await InParallelAsync(
            () => owner.PostAsJsonAsync("/api/payrolls/2026-08/reopen", new { reason = "Sai công" }),
            () => owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", new { amountPaid = 100_000, paymentDate = "2026-09-01", method = "Cash", referenceCode = "A" }),
            () => owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", new { amountPaid = 100_000, paymentDate = "2026-09-01", method = "Cash", referenceCode = "B" }));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        await using var check = database.CreateContext();
        var saved = await check.Payrolls.AsNoTracking().Include(p => p.Settlements).ThenInclude(s => s.Payments).SingleAsync(p => p.Period == "2026-08");
        var slip = Assert.Single(saved.Settlements);
        if (saved.FinalizedAt is null)
        {
            Assert.Empty(slip.Payments);
            Assert.Equal(StatusIds.LaborSettlement.Draft, slip.StatusId);
        }
        else
        {
            Assert.NotEmpty(slip.Payments);
            Assert.Equal(StatusIds.LaborSettlement.Approved, slip.StatusId);
            Assert.True(slip.Payments.Sum(p => p.AmountPaid) <= slip.TotalObligation);
        }
    }

    [SqlServerFact]
    public async Task Parallel_new_pay_rates_of_one_person_never_leave_overlapping_versions()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, _) = await OwnerWithCooperativeAsync(api);
        var worker = new SeasonalWorker { FullName = $"Vũ Văn {NewCode()}", PhoneNumber = NewPhone() };
        await ChangeAsync(api, db =>
        {
            db.SeasonalWorkers.Add(worker);
            db.PayRuleVersions.Add(new PayRuleVersion
            {
                WorkerId = worker.Id, SalaryType = SalaryType.DailyWage, Amount = 280_000, ValidFrom = new DateOnly(2026, 1, 1),
                EnteredByAccountId = account.Id, StatusId = StatusIds.PayRule.Approved,
            });
        });
        object Rate(string from, int amount) => new { workerId = worker.Id, salaryType = "DailyWage", amount, validFrom = from };

        var sameDay = await InParallelAsync(Enumerable.Range(0, 3).Select(i => (Func<Task<HttpResponseMessage>>)(() =>
            owner.PostAsJsonAsync("/api/pay-rates", Rate("2026-03-01", 300_000 + (i * 10_000))))).ToArray());
        var otherDays = await InParallelAsync(
            () => owner.PostAsJsonAsync("/api/pay-rates", Rate("2026-05-01", 320_000)),
            () => owner.PostAsJsonAsync("/api/pay-rates", Rate("2026-06-01", 330_000)));

        Assert.Single(sameDay, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in sameDay.Concat(otherDays).Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.True(await refused.ErrorCodeAsync() is "MSG03" or "MSG06", await refused.Content.ReadAsStringAsync());
        }

        await using var check = database.CreateContext();
        var versions = await check.PayRuleVersions.AsNoTracking().Where(v => v.WorkerId == worker.Id).OrderBy(v => v.ValidFrom).ToListAsync();
        for (var i = 1; i < versions.Count; i++)
        {
            Assert.True(versions[i - 1].ValidTo is { } end && end < versions[i].ValidFrom,
                $"{versions[i - 1].ValidFrom}–{versions[i - 1].ValidTo} overlaps {versions[i].ValidFrom}–{versions[i].ValidTo}");
        }

        Assert.Single(versions, v => v.ValidTo is null);
    }

    [SqlServerFact]
    public async Task Database_refuses_labor_rows_that_break_the_thousand_vnd_and_one_person_rules()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (_, account, cooperative) = await OwnerWithCooperativeAsync(api);
        var (first, second) = (await AddZoneAsync(api, cooperative.Id), await AddZoneAsync(api, cooperative.Id));
        var worker = new SeasonalWorker { FullName = $"Kiều Văn {NewCode()}", PhoneNumber = NewPhone() };
        var employee = new Employee { CooperativeId = cooperative.Id, FullName = $"Kiều Thị {NewCode()}", PhoneNumber = NewPhone() };
        await ChangeAsync(api, db =>
        {
            db.SeasonalWorkers.Add(worker);
            db.Employees.Add(employee);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                ZoneId = first.Id, WorkerId = worker.Id, WorkDate = Month, Result = AttendanceResult.Present, RecordedByAccountId = account.Id,
            });
        });

        Task Refused(Action<OMTS.Infrastructure.Persistence.OmtsDbContext> change) => Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(api, change));

        await Refused(db => db.AttendanceRecords.Add(new AttendanceRecord
        {
            ZoneId = second.Id, WorkerId = worker.Id, WorkDate = Month, Result = AttendanceResult.Present, RecordedByAccountId = account.Id,
        }));
        await Refused(db => db.AttendanceRecords.Add(new AttendanceRecord
        {
            ZoneId = second.Id, WorkerId = worker.Id, EmployeeId = employee.Id, WorkDate = Month.AddDays(1), Result = AttendanceResult.Present, RecordedByAccountId = account.Id,
        }));
        await Refused(db => db.PayLines.Add(new PayLine { WorkerId = worker.Id, PayDate = Month, LineType = PayLineType.DailyWage, Amount = 92_500 }));
        await Refused(db => db.LaborPayments.Add(new LaborPayment
        {
            IsAdvance = true, Recipient = "x", AmountPaid = 100_000, PaymentDate = Month, Method = PaymentMethod.Cash,
        }));
        await Refused(db => db.LaborPayments.Add(new LaborPayment
        {
            IsAdvance = true, WorkerId = worker.Id, Recipient = "x", AmountPaid = 100_500, PaymentDate = Month, Method = PaymentMethod.Cash,
        }));
        await Refused(db => db.WorkAssignments.Add(new WorkAssignment
        {
            ZoneId = first.Id, WorkerId = worker.Id, EmployeeId = employee.Id, WorkDate = Month, Task = "x", PaymentType = WorkPaymentType.DailyWage,
        }));
        await Refused(db => db.WorkAssignments.Add(new WorkAssignment
        {
            ZoneId = first.Id, WorkerId = worker.Id, WorkDate = Month, Task = "x", PaymentType = WorkPaymentType.Piecework, PieceUnit = "rope", PlannedQuantity = 10,
        }));

        await using var check = database.CreateContext();
        Assert.Equal(1, await check.AttendanceRecords.CountAsync(r => r.WorkerId == worker.Id));
        Assert.Equal(0, await check.LaborPayments.CountAsync());
        Assert.Equal(0, await check.WorkAssignments.CountAsync());
    }

    [SqlServerFact]
    public async Task Migration_refused_by_old_team_names_that_differ_only_in_case_leaves_no_half_applied_schema()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_AddWorkforceAndPayroll", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(before);
        await db.Database.ExecuteSqlAsync($"INSERT INTO Contractors (Id, Name, StatusId, CreatedAt) VALUES ({Guid.NewGuid()}, N'Tổ Cũ', 101, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync($"INSERT INTO Contractors (Id, Name, StatusId, CreatedAt) VALUES ({Guid.NewGuid()}, N'TỔ CŨ', 101, SYSDATETIMEOFFSET())");

        await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync());

        Assert.Equal(before, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Payrolls'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'WorkAssignments' AND COLUMN_NAME = 'PieceUnit'").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM Contractors").SingleAsync());
    }
}
