using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The CultivationLogs migration (PR C4) on SQL Server: Up adds the two tables, Down gives back the schema it found, and Up runs again.</summary>
public class CultivationLogsMigrationTests
{
    private const string MigrationBefore = "20261104113119_CultivationCyclesV2";

    private const string TablesOfLogs = "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN ('CultivationLogs', 'CultivationLogPhotos')";

    private const string SchemaQuery = """
        SELECT CONCAT('table ', t.name) AS [Value] FROM sys.tables t
        UNION ALL SELECT CONCAT('index ', OBJECT_NAME(i.object_id), ' ', i.name) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE i.type > 0
        UNION ALL SELECT CONCAT('fk ', f.name, ' ', f.delete_referential_action) FROM sys.foreign_keys f
        UNION ALL SELECT CONCAT('check ', c.name) FROM sys.check_constraints c
        """;

    private static async Task<List<string>> SchemaAsync(OmtsDbContext db) =>
        (await db.Database.SqlQueryRaw<string>(SchemaQuery).ToListAsync()).Order(StringComparer.Ordinal).ToList();

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    [SqlServerFact]
    public async Task Up_down_and_up_again_keep_the_cycles_and_end_with_no_pending_model_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var oldSchema = await SchemaAsync(db);
        Assert.Equal(0, await ScalarAsync(db, TablesOfLogs));

        var zone = new FarmingZone { CooperativeId = await OldSchemaRows.CooperativeAsync(db), Code = "KV01", Name = "A" };
        var cycle = new CultivationCycle { Raft = new Raft { Zone = zone, Code = "B01", Size = "1" }, StartDate = new DateOnly(2026, 1, 1) };
        var account = new Account { Username = "owner", DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner, StatusId = StatusIds.Account.Active };
        db.AddRange(cycle, account);
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(2, await ScalarAsync(db, TablesOfLogs));
        var upSchema = await SchemaAsync(db);
        Assert.Contains("fk FK_CultivationLogs_CultivationCycles_CycleId 0", upSchema);
        Assert.Contains("fk FK_CultivationLogs_Accounts_RecordedByAccountId 0", upSchema);
        Assert.Contains("fk FK_CultivationLogPhotos_CultivationLogs_LogId 0", upSchema);
        Assert.Contains("check CK_CultivationLogPhoto_SortOrder", upSchema);
        Assert.Contains("index CultivationLogPhotos IX_CultivationLogPhotos_LogId_SortOrder", upSchema);
        Assert.Contains("index CultivationLogs IX_CultivationLogs_CycleId_VisitDate", upSchema);

        var log = new CultivationLog
        {
            CycleId = cycle.Id, VisitDate = new DateOnly(2026, 2, 1), RecordedByAccountId = account.Id,
            Photos = [new CultivationLogPhoto { SortOrder = 1, FileKey = "cultivation-log-photos/2026/02/a.jpg" }],
        };
        db.CultivationLogs.Add(log);
        await db.SaveChangesAsync();

        // Dev and demo data only: Down drops the logs with their tables; the cycle stays.
        await migrator.MigrateAsync(MigrationBefore);

        Assert.Equal(oldSchema, await SchemaAsync(db));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM CultivationCycles"));

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(upSchema, await SchemaAsync(db));
        Assert.Equal(0, await db.CultivationLogs.CountAsync());
        Assert.Equal(1, await db.CultivationCycles.CountAsync());
    }
}
