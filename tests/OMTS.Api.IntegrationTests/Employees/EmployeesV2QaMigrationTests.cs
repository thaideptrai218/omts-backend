using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>QA of OMTS-227, the migration EmployeesV2 beyond the branch's Up, Down, Up: rows in the dropped tables, the script form, the audit link.</summary>
public class EmployeesV2QaMigrationTests
{
    private static (IMigrator Migrator, string Own, string Previous) Plan(OmtsDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var own = all.Single(m => m.EndsWith("_EmployeesV2", StringComparison.Ordinal));
        return (db.GetService<IMigrator>(), own, all[all.IndexOf(own) - 1]);
    }

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    // Design §5 "Keep: the guard THROW when the old scaffold tables hold rows before dropping"; §TL;DR the same.
    [SqlServerFact(Skip = "Accepted: PM 03/11, every environment holds dev and demo data only, so dropped tables need no guard.")]
    public async Task L8_up_refuses_to_drop_scaffold_tables_that_hold_rows()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var (migrator, own, previous) = Plan(db);
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Contractors (Id, StatusId, CreatedAt, Name) VALUES (NEWID(), 101, SYSDATETIMEOFFSET(), N'Nhà thầu có dữ liệu')");

        var failure = await Record.ExceptionAsync(() => migrator.MigrateAsync(own));

        Assert.NotNull(failure);
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Contractors"));
    }

    // The tables stay readable by nothing: no entity, no set, the check helper removed; Down gives them back with their checks.
    [SqlServerFact]
    public async Task Down_recreates_the_scaffold_tables_with_their_checks_and_up_drops_them_again_when_empty()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var (migrator, own, previous) = Plan(db);
        await migrator.MigrateAsync(own);

        Assert.Empty(db.Model.GetEntityTypes().Where(t => t.ClrType.Name is "SeasonalWorker" or "Contractor" or "AttendanceRecord"));
        Assert.Single(db.Model.GetEntityTypes(), t => t.ClrType.Name == "JobType");

        await migrator.MigrateAsync(previous);

        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_AttendanceRecord_OnePerson'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_WorkAssignment_PaymentType'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_Employees_FullName_PhoneNumber' AND is_unique = 1"));

        await migrator.MigrateAsync(own);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // The hand-written SQL, with rows, as a deployment script: batches run one by one, like sqlcmd.
    [SqlServerFact]
    public async Task The_idempotent_script_of_the_migration_runs_with_rows_and_twice()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var (migrator, own, previous) = Plan(db);
        await migrator.MigrateAsync(previous);
        var row = new OMTS.Domain.Entities.Identity.Cooperative { FullName = "HTX" };
        db.Cooperatives.Add(row);
        await db.SaveChangesAsync();
        var phone = NewPhone();
        foreach (var (name, createdAt) in new[] { ("Một", "2026-01-01"), ("Hai", "2026-02-01") })
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO Employees (Id, CooperativeId, FullName, PhoneNumber, StatusId, CreatedAt) VALUES (NEWID(), {row.Id}, {name}, {phone}, 401, {createdAt})");
        }

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

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.EmployeeTypePeriods.CountAsync());
        Assert.Equal(1, await db.Employees.CountAsync(e => e.PhoneNumber == phone));
        Assert.DoesNotContain(own, await db.Database.GetPendingMigrationsAsync());
    }

    // Nothing writes ActorEmployeeId yet, so the new key cannot fail a save; a row of an Account actor stays free of it, and a delete is restricted.
    [SqlServerFact]
    public async Task The_new_key_keeps_an_employee_with_audit_rows_and_leaves_account_rows_alone()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var cooperative = new OMTS.Domain.Entities.Identity.Cooperative { FullName = "HTX" };
        var employee = new OMTS.Domain.Entities.Identity.Employee { Cooperative = cooperative, FullName = "Có audit" };
        db.Employees.Add(employee);
        db.AuditEvents.Add(new OMTS.Domain.Entities.Platform.AuditEvent { Action = "A.Done", RecordType = "A", OccurredAt = DateTimeOffset.UtcNow, ActorEmployeeId = employee.Id });
        db.AuditEvents.Add(new OMTS.Domain.Entities.Platform.AuditEvent { Action = "A.Done", RecordType = "A", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync("DELETE FROM Employees WHERE Id = {0}", employee.Id));

        Assert.Contains("FK_AuditEvents_Employees_ActorEmployeeId", refused?.Message);
        Assert.Equal(2, await db.AuditEvents.CountAsync());
    }
}
