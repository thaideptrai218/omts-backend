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

/// <summary>The SeedingRecords migration (PR C2) on SQL Server: Up replaces the seeding events, Down gives back the schema it found, and Up runs again.</summary>
public class SeedingRecordsMigrationTests
{
    private const string MigrationBefore = "20261104164709_MaterialUsages";

    private const string Tables = "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN ('SeedingRecords', 'SeedingRecordLanes')";

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
    public async Task Up_down_and_up_again_keep_the_cycles_rafts_and_receipts_and_end_with_no_pending_model_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var oldSchema = await SchemaAsync(db);
        Assert.Equal(0, await ScalarAsync(db, Tables));
        Assert.Contains("table SeedingEvents", oldSchema);

        var zone = new FarmingZone { Cooperative = new Cooperative { FullName = "HTX" }, Code = "KV01", Name = "A" };
        var lane = new Lane { LaneNumber = 1 };
        var cycle = new CultivationCycle { Raft = new Raft { Zone = zone, Code = "B01", Size = "1", Lanes = [lane] }, StartDate = new DateOnly(2026, 1, 1) };
        var account = new Account { Username = "owner", DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner, StatusId = StatusIds.Account.Active };
        var item = new FarmingInputItem { Code = "VT001", Name = "Seed", Group = InputItemGroup.Seed, Unit = "con" };
        var receipt = new PurchaseReceipt { Code = "PN000001", Supplier = new Supplier { Name = "S", PhoneNumber = "0900000002" }, ReceiptDate = new DateOnly(2026, 1, 1), RecordedByAccountId = Guid.Empty, StatusId = StatusIds.PurchaseReceipt.Completed };
        db.AddRange(cycle, account, item, receipt);
        receipt.RecordedByAccountId = account.Id;
        var line = new PurchaseReceiptLine { Receipt = receipt, LineNumber = 1, Item = item, Unit = "con", Quantity = 10, UnitPrice = 1 };
        db.Add(line);
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(2, await ScalarAsync(db, Tables));
        var upSchema = await SchemaAsync(db);
        Assert.DoesNotContain("table SeedingEvents", upSchema);
        Assert.Contains("fk FK_SeedingRecords_CultivationCycles_CycleId 0", upSchema);
        Assert.Contains("fk FK_SeedingRecords_PurchaseReceiptLines_SeedLotLineId 0", upSchema);
        Assert.Contains("fk FK_SeedingRecords_Accounts_RecordedByAccountId 0", upSchema);
        Assert.Contains("fk FK_SeedingRecordLanes_SeedingRecords_SeedingRecordId 0", upSchema);
        Assert.Contains("fk FK_SeedingRecordLanes_Lanes_LaneId 0", upSchema);
        Assert.Contains("check CK_SeedingRecord_Quantity", upSchema);
        Assert.Contains("index SeedingRecords IX_SeedingRecords_CycleId_SeedingDate", upSchema);
        Assert.Contains("index SeedingRecords IX_SeedingRecords_SeedLotLineId", upSchema);

        var record = new SeedingRecord
        {
            CycleId = cycle.Id, SeedingDate = new DateOnly(2026, 1, 2), SeedLotLineId = line.Id, Quantity = 5, RecordedByAccountId = account.Id,
            Lanes = [new SeedingRecordLane { LaneId = lane.Id }],
        };
        db.SeedingRecords.Add(record);
        await db.SaveChangesAsync();

        // Dev and demo data only: Down drops the sessions with their tables; the cycle, lane and receipt line stay.
        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(oldSchema, await SchemaAsync(db));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM CultivationCycles"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Lanes"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM PurchaseReceiptLines"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM SeedingEvents"));

        db.ChangeTracker.Clear();
        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(upSchema, await SchemaAsync(db));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM SeedingRecords"));
    }
}
