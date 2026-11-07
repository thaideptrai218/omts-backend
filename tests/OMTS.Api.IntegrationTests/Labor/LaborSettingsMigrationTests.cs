using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>The migration LaborSettings on a real SQL Server: an existing cooperative starts with the SRS defaults, the checks and indexes hold, and it reverts and applies again with rows in the tables.</summary>
public class LaborSettingsMigrationTests
{
    private static readonly string[] Added = ["PayRates", "AttendanceSettingsVersions", "Payrolls"];

    [SqlServerFact]
    public async Task Up_down_up_gives_an_existing_cooperative_the_defaults_and_the_checks_hold()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var own = all.Single(m => m.EndsWith("_LaborSettings", StringComparison.Ordinal));
        var previous = all[all.IndexOf(own) - 1];
        await migrator.MigrateAsync(previous);

        var cooperative = Guid.NewGuid();
        await ExecuteAsync(db, $"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ('{cooperative}', N'HTX', SYSDATETIMEOFFSET())");
        var jobType = Guid.NewGuid();
        await ExecuteAsync(db, $"INSERT INTO JobTypes (Id, Name, StatusId, CreatedAt) VALUES ('{jobType}', N'Thu hoạch', 101, SYSDATETIMEOFFSET())");
        var owner = Guid.NewGuid();
        await ExecuteAsync(db, $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt, NotificationPreferences) VALUES ('{owner}', N'{NewCode()}', N'O', N'{NewPassword()}', N'CooperativeOwner', 301, 0, 0, SYSDATETIMEOFFSET(), N'[]')");

        await migrator.MigrateAsync(own);

        Assert.Empty(Added.Except(await TablesAsync(db)));
        Assert.Equal(1, await ScalarAsync(db, "SELECT PayPeriodStartDay AS [Value] FROM Cooperatives"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT CAST(AutomaticPayroll AS int) AS [Value] FROM Cooperatives"));
        Assert.Equal(4, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE [Group] = N'PAYROLL' AND Id BETWEEN 3401 AND 3404"));

        await AssertRefusedAsync(db, "UPDATE Cooperatives SET PayPeriodStartDay = 29", "CK_Cooperatives_PayPeriodStartDay");
        await AssertRefusedAsync(db, "UPDATE Cooperatives SET PayPeriodStartDay = 0", "CK_Cooperatives_PayPeriodStartDay");

        string Rate(decimal amount, string date) => $"INSERT INTO PayRates (Id, JobTypeId, HourlyRate, EffectiveFrom, EnteredByAccountId, CreatedAt) VALUES (NEWID(), '{jobType}', {amount}, '{date}', '{owner}', SYSDATETIMEOFFSET())";
        await ExecuteAsync(db, Rate(40000, "2026-01-01"));
        await AssertRefusedAsync(db, Rate(50000, "2026-01-01"), "IX_PayRates_JobTypeId_EffectiveFrom");
        await AssertRefusedAsync(db, Rate(0, "2026-02-01"), "CK_PayRates_HourlyRate");

        string Settings(string date, string start, string end) => $"INSERT INTO AttendanceSettingsVersions (Id, EffectiveFrom, BreakStart, BreakEnd, RoundingUnit, EnteredByAccountId, CreatedAt) VALUES (NEWID(), '{date}', {start}, {end}, N'None', '{owner}', SYSDATETIMEOFFSET())";
        await ExecuteAsync(db, Settings("2026-01-01", "'11:30'", "'12:30'"));
        await AssertRefusedAsync(db, Settings("2026-01-01", "NULL", "NULL"), "IX_AttendanceSettingsVersions_EffectiveFrom");
        await AssertRefusedAsync(db, Settings("2026-02-01", "'12:30'", "'11:30'"), "CK_AttendanceSettingsVersions_Break");
        await AssertRefusedAsync(db, Settings("2026-02-01", "'12:30'", "NULL"), "CK_AttendanceSettingsVersions_Break");

        string Payroll(string start, string end, int status = 3401) => $"INSERT INTO Payrolls (Id, PeriodStart, PeriodEnd, StatusId, CreatedBySystem, CreatedAt) VALUES (NEWID(), '{start}', '{end}', {status}, 0, SYSDATETIMEOFFSET())";
        await ExecuteAsync(db, Payroll("2026-10-01", "2026-10-31", 3404));
        await AssertRefusedAsync(db, Payroll("2026-10-01", "2026-10-30"), "IX_Payrolls_PeriodStart");
        await AssertRefusedAsync(db, Payroll("2026-10-02", "2026-10-31"), "IX_Payrolls_PeriodEnd");
        await AssertRefusedAsync(db, Payroll("2026-12-31", "2026-12-01"), "CK_Payrolls_Period");
        await AssertRefusedAsync(db, Payroll("2027-01-01", "2027-01-31", 9999), "FK_Payrolls_Statuses_StatusId");

        await migrator.MigrateAsync(previous);

        Assert.Empty(Added.Intersect(await TablesAsync(db)));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE Id BETWEEN 3401 AND 3404"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Cooperatives') AND name IN ('PayPeriodStartDay', 'AutomaticPayroll')"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM JobTypes"));

        await migrator.MigrateAsync(own);
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(Added.Except(await TablesAsync(db)));
        Assert.Equal(1, await db.Cooperatives.Select(c => c.PayPeriodStartDay).SingleAsync());
    }

    private static async Task AssertRefusedAsync(OmtsDbContext db, string sql, string constraint)
    {
        var refused = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(db, sql));
        Assert.Contains(constraint, refused.Message);
    }

    private static async Task ExecuteAsync(OmtsDbContext db, string sql) => await db.Database.ExecuteSqlRawAsync(sql);

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    private static Task<List<string>> TablesAsync(OmtsDbContext db) => db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables").ToListAsync();
}
