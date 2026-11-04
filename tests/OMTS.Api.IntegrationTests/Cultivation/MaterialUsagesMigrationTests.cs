using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The MaterialUsages migration (PR C3) on SQL Server: Up adds the table, Down gives back the schema it found, and Up runs again.</summary>
public class MaterialUsagesMigrationTests
{
    private const string MigrationBefore = "20261104124405_CultivationLogs";

    private const string SchemaQuery = """
        SELECT CONCAT('table ', t.name) AS [Value] FROM sys.tables t
        UNION ALL SELECT CONCAT('index ', OBJECT_NAME(i.object_id), ' ', i.name) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE i.type > 0
        UNION ALL SELECT CONCAT('fk ', f.name, ' ', f.delete_referential_action) FROM sys.foreign_keys f
        UNION ALL SELECT CONCAT('check ', c.name) FROM sys.check_constraints c
        """;

    private static async Task<List<string>> SchemaAsync(OmtsDbContext db) =>
        (await db.Database.SqlQueryRaw<string>(SchemaQuery).ToListAsync()).Order(StringComparer.Ordinal).ToList();

    [SqlServerFact]
    public async Task Up_down_and_up_again_keep_the_rafts_and_items_and_end_with_no_pending_model_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var oldSchema = await SchemaAsync(db);
        Assert.DoesNotContain("table MaterialUsages", oldSchema);

        var raft = new Raft { Zone = new FarmingZone { CooperativeId = await OldSchemaRows.CooperativeAsync(db), Code = "KV01", Name = "A" }, Code = "B01", Size = "1" };
        var item = new FarmingInputItem { Code = "MAT", Name = "Buoy", Group = MaterialUsage.ItemGroup, Unit = "piece" };
        var account = new Account { Username = "owner", DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner, StatusId = StatusIds.Account.Active };
        db.AddRange(raft, item, account);
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        var upSchema = await SchemaAsync(db);
        Assert.Contains("table MaterialUsages", upSchema);
        Assert.Contains("fk FK_MaterialUsages_Rafts_RaftId 0", upSchema);
        Assert.Contains("fk FK_MaterialUsages_FarmingInputItems_ItemId 0", upSchema);
        Assert.Contains("fk FK_MaterialUsages_Accounts_RecordedByAccountId 0", upSchema);
        Assert.Contains("check CK_MaterialUsage_Quantity", upSchema);
        Assert.Contains("index MaterialUsages IX_MaterialUsages_RaftId_UsageDate", upSchema);

        db.MaterialUsages.Add(new MaterialUsage
        {
            RaftId = raft.Id, ItemId = item.Id, UsageDate = new DateOnly(2026, 2, 1), RepairReason = "broken buoy", Quantity = 1.125m, RecordedByAccountId = account.Id,
        });
        await db.SaveChangesAsync();

        // Dev and demo data only: Down drops the usage with its table; the raft and the item stay.
        await migrator.MigrateAsync(MigrationBefore);

        Assert.Equal(oldSchema, await SchemaAsync(db));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Rafts").SingleAsync());

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(upSchema, await SchemaAsync(db));
        Assert.Equal(0, await db.MaterialUsages.CountAsync());
        Assert.Equal(1, await db.FarmingInputItems.CountAsync());
    }
}
