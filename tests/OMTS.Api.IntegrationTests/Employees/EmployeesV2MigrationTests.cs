using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// The migration EmployeesV2 on a real SQL Server: the scaffold tables are gone, demo rows reach the new rules (first type,
/// one Employed holder of a number), and it reverts and applies again with rows in the tables.
/// </summary>
public class EmployeesV2MigrationTests
{
    private static readonly string[] Dropped = ["AttendanceRecords", "WorkAssignments", "SeasonalWorkers", "Contractors"];
    private static readonly string[] Added = ["JobTypes", "EmployeeJobTypes", "EmployeeTypePeriods"];

    [SqlServerFact]
    public async Task Up_down_up_keeps_employees_gives_them_a_type_and_makes_the_index_hold()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var own = all.Single(m => m.EndsWith("_EmployeesV2", StringComparison.Ordinal));
        var previous = all[all.IndexOf(own) - 1];
        await migrator.MigrateAsync(previous);

        // By SQL: later migrations add columns of the model that this one does not have yet.
        var cooperative = Guid.NewGuid();
        await ExecuteAsync(db, $"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ('{cooperative}', N'HTX', SYSDATETIMEOFFSET())");
        var phone = NewPhone();
        await InsertEmployeeAsync(db, cooperative, "Một", phone, 401, "2026-01-01");
        await InsertEmployeeAsync(db, cooperative, "Hai", phone, 401, "2026-02-01");
        await InsertEmployeeAsync(db, cooperative, "Đã nghỉ", phone, 402, "2026-03-01");

        await migrator.MigrateAsync(own);

        var tables = await TablesAsync(db);
        Assert.Empty(Dropped.Intersect(tables));
        Assert.Empty(Added.Except(tables));
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM EmployeeTypePeriods WHERE [Type] = N'Staff'"));
        // The first of the Employed employees sharing a number keeps it; a Left one keeps theirs.
        Assert.Equal(2, await ScalarAsync(db, $"SELECT COUNT(*) AS [Value] FROM Employees WHERE PhoneNumber = '{phone}'"));
        Assert.Equal(1, await ScalarAsync(db, $"SELECT COUNT(*) AS [Value] FROM Employees WHERE PhoneNumber = '{phone}' AND StatusId = 401 AND FullName = N'Một'"));
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => InsertEmployeeAsync(db, cooperative, "Ba", phone, 401, "2026-04-01"));
        Assert.Contains("UX_Employees_PhoneNumber_Employed", duplicate.Message);
        await InsertEmployeeAsync(db, cooperative, "Một", phone, 402, "2026-05-01");
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = 'FK_AuditEvents_Employees_ActorEmployeeId'"));

        await migrator.MigrateAsync(previous);

        tables = await TablesAsync(db);
        Assert.Empty(Dropped.Except(tables));
        Assert.Empty(Added.Intersect(tables));
        Assert.Equal(4, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Employees"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Employees') AND name = 'EmploymentEndDate'"));

        await migrator.MigrateAsync(own);
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(Dropped.Intersect(await TablesAsync(db)));
        Assert.Equal(4, await db.Employees.CountAsync());
        Assert.Equal(4, await db.EmployeeTypePeriods.CountAsync());
    }

    private static Task InsertEmployeeAsync(OmtsDbContext db, Guid cooperative, string name, string phone, int statusId, string createdAt) => ExecuteAsync(
        db,
        $"INSERT INTO Employees (Id, CooperativeId, FullName, PhoneNumber, StatusId, CreatedAt) VALUES (NEWID(), '{cooperative}', N'{name}', '{phone}', {statusId}, '{createdAt}')");

    private static async Task ExecuteAsync(OmtsDbContext db, string sql) => await db.Database.ExecuteSqlRawAsync(sql);

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    private static Task<List<string>> TablesAsync(OmtsDbContext db) => db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables").ToListAsync();
}
