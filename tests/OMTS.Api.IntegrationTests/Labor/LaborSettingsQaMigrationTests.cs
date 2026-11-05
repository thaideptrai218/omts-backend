using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>QA of OMTS-227, the migration LaborSettings beyond the branch's Up, Down, Up: the hand-edited default, rows in every new table on the way down, the script form.</summary>
public class LaborSettingsQaMigrationTests
{
    private static (IMigrator Migrator, string Own, string Previous) Plan(OmtsDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var own = all.Single(m => m.EndsWith("_LaborSettings", StringComparison.Ordinal));
        return (db.GetService<IMigrator>(), own, all[all.IndexOf(own) - 1]);
    }

    private static async Task ExecuteAsync(OmtsDbContext db, string sql) => await db.Database.ExecuteSqlRawAsync(sql);

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    private static Task<string> TextAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<string>(sql).SingleAsync();

    private const string DefaultOfStartDay =
        "SELECT dc.definition AS [Value] FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id " +
        "WHERE dc.parent_object_id = OBJECT_ID('Cooperatives') AND c.name = 'PayPeriodStartDay'";

    private static string InsertCooperative(Guid id) => $"INSERT INTO Cooperatives (Id, FullName, StatusId, CreatedAt) VALUES ('{id}', N'HTX', 201, SYSDATETIMEOFFSET())";

    [SqlServerFact]
    public async Task The_hand_edited_default_is_1_after_every_up_and_a_row_that_leaves_it_out_gets_it()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var (migrator, own, previous) = Plan(db);
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync(own);
        Assert.Equal("((1))", await TextAsync(db, DefaultOfStartDay));

        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync(own);
        Assert.Equal("((1))", await TextAsync(db, DefaultOfStartDay));

        var id = Guid.NewGuid();
        await ExecuteAsync(db, InsertCooperative(id));
        Assert.Equal(1, await ScalarAsync(db, $"SELECT PayPeriodStartDay AS [Value] FROM Cooperatives WHERE Id = '{id}'"));
        Assert.Equal(0, await ScalarAsync(db, $"SELECT CAST(AutomaticPayroll AS int) AS [Value] FROM Cooperatives WHERE Id = '{id}'"));
    }

    [SqlServerFact]
    public async Task Down_with_rows_in_every_new_table_keeps_the_job_types_and_up_gives_back_the_defaults_and_empty_tables()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var (migrator, own, previous) = Plan(db);
        await migrator.MigrateAsync(own);
        await ExecuteAsync(db, InsertCooperative(Guid.NewGuid()));
        await ExecuteAsync(db, "UPDATE Cooperatives SET PayPeriodStartDay = 26, AutomaticPayroll = 1");
        var (jobType, owner) = (Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, $"INSERT INTO JobTypes (Id, Name, StatusId, CreatedAt) VALUES ('{jobType}', N'Thu hoạch', 101, SYSDATETIMEOFFSET())");
        await ExecuteAsync(db, $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt, NotificationPreferences) VALUES ('{owner}', N'{NewCode()}', N'O', N'{NewPassword()}', N'CooperativeOwner', 301, 0, 0, SYSDATETIMEOFFSET(), N'[]')");
        await ExecuteAsync(db, $"INSERT INTO PayRates (Id, JobTypeId, HourlyRate, EffectiveFrom, EnteredByAccountId, CreatedAt) VALUES (NEWID(), '{jobType}', 40000, '2026-01-01', '{owner}', SYSDATETIMEOFFSET())");
        await ExecuteAsync(db, $"INSERT INTO AttendanceSettingsVersions (Id, EffectiveFrom, BreakStart, BreakEnd, RoundingUnit, EnteredByAccountId, CreatedAt) VALUES (NEWID(), '2026-01-01', '11:30', '12:30', N'Minutes15', '{owner}', SYSDATETIMEOFFSET())");
        foreach (var status in new[] { 3401, 3402, 3403, 3404 })
        {
            await ExecuteAsync(db, $"INSERT INTO Payrolls (Id, PeriodStart, PeriodEnd, StatusId, CreatedBySystem, SentByAccountId, CreatedAt) VALUES (NEWID(), DATEADD(month, {status - 3401}, '2026-01-01'), DATEADD(day, -1, DATEADD(month, {status - 3400}, '2026-01-01')), {status}, 0, '{owner}', SYSDATETIMEOFFSET())");
        }

        await migrator.MigrateAsync(previous);

        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM JobTypes"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE Id BETWEEN 3401 AND 3404"));

        await migrator.MigrateAsync(own);

        Assert.Equal(1, await ScalarAsync(db, "SELECT PayPeriodStartDay AS [Value] FROM Cooperatives"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT CAST(AutomaticPayroll AS int) AS [Value] FROM Cooperatives"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Payrolls"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM PayRates"));
        Assert.Equal(4, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE [Group] = N'PAYROLL'"));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // As a deployment script: batches run one by one, like sqlcmd.
    [SqlServerFact]
    public async Task The_idempotent_script_runs_with_a_cooperative_and_twice()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var (migrator, own, previous) = Plan(db);
        await migrator.MigrateAsync(previous);
        await ExecuteAsync(db, InsertCooperative(Guid.NewGuid()));

        var script = migrator.GenerateScript(previous, own, MigrationsSqlGenerationOptions.Idempotent);
        foreach (var run in new[] { 1, 2 })
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            foreach (var batch in script.Split(["\r\nGO\r\n", "\nGO\n"], StringSplitOptions.RemoveEmptyEntries).Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                await using var command = new SqlCommand(batch, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        Assert.Equal(1, await ScalarAsync(db, "SELECT PayPeriodStartDay AS [Value] FROM Cooperatives"));
        Assert.Equal(4, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE [Group] = N'PAYROLL'"));
        Assert.DoesNotContain(own, await db.Database.GetPendingMigrationsAsync());
    }

    // The two tests L2 changed in L1's migration tests insert by SQL what they inserted through the model: the row must be what the model wrote.
    [Fact]
    public void The_status_those_tests_write_by_hand_is_the_default_of_the_model() =>
        Assert.Equal(201, new OMTS.Domain.Entities.Identity.Cooperative().StatusId);
}
