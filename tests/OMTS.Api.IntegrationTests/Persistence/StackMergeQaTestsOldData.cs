using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Persistence;

/// <summary>
/// QA of the merge on a database of the old model, as develop left it: rows in every table that both sides rework. The interleave runs the purchase rework before the raft rework, an order
/// neither side tried with data, as each side's own migration test starts at the step before its own.
/// </summary>
public class StackMergeQaTestsOldData
{
    private const string BeforeTheStack = "20261028151320_RemoveLegacyScaffolding";

    /// <summary>One row: the given columns, and for every other required column a plain value of its type.</summary>
    private static async Task InsertAsync(OmtsDbContext db, string table, IReadOnlyDictionary<string, object> values)
    {
        var columns = await db.Database.SqlQueryRaw<RequiredColumn>(
            "SELECT c.name AS Name, t.name AS TypeName FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID({0}) AND c.is_nullable = 0 AND c.default_object_id = 0 AND c.is_identity = 0 AND t.name <> 'timestamp'",
            table).ToListAsync();
        var existing = await db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({0})", table).ToListAsync();
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values.Where(v => existing.Contains(v.Key, StringComparer.OrdinalIgnoreCase)))
        {
            row[name] = value;
        }

        foreach (var column in columns.Where(c => !row.ContainsKey(c.Name)))
        {
            row[column.Name] = column.TypeName switch
            {
                "uniqueidentifier" => Guid.NewGuid(),
                "nvarchar" or "varchar" => column.Name,
                "int" or "bigint" or "smallint" or "tinyint" => 0,
                "decimal" => 0m,
                "bit" => false,
                "date" => new DateTime(2026, 1, 1),
                "datetimeoffset" => DateTimeOffset.UtcNow,
                "datetime2" => DateTime.UtcNow,
                _ => throw new InvalidOperationException($"{table}.{column.Name} is {column.TypeName}"),
            };
        }

        var names = row.Keys.ToList();
        var sql = $"INSERT INTO [{table}] ({string.Join(", ", names.Select(n => $"[{n}]"))}) VALUES ({string.Join(", ", names.Select((_, i) => $"@p{i}"))})";
        await db.Database.ExecuteSqlRawAsync(sql, names.Select((n, i) => (object)new SqlParameter($"@p{i}", row[n])).ToArray());
    }

    private sealed record RequiredColumn(string Name, string TypeName);

    [SqlServerFact]
    public async Task A_database_of_the_old_model_with_rows_in_every_reworked_table_reaches_the_head_and_keeps_its_records()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeTheStack);

        Guid New() => Guid.NewGuid();
        var (account, cooperative, zone, raft, laneOne, laneTwo, supplier) = (New(), New(), New(), New(), New(), New(), New());
        var (seedItem, materialItem, receipt, seedLine, materialLine, lot, cycle, entry, returnId) = (New(), New(), New(), New(), New(), New(), New(), New(), New());
        var at = new DateTimeOffset(2026, 3, 10, 2, 0, 0, TimeSpan.Zero);

        await InsertAsync(db, "Accounts", new Dictionary<string, object> { ["Id"] = account, ["Username"] = "owner", ["DisplayName"] = "Owner", ["SubjectType"] = "CooperativeOwner", ["StatusId"] = StatusIds.Account.Active });
        await InsertAsync(db, "Cooperatives", new Dictionary<string, object> { ["Id"] = cooperative, ["FullName"] = "HTX", ["StatusId"] = 201 });
        await InsertAsync(db, "FarmingZones", new Dictionary<string, object> { ["Id"] = zone, ["CooperativeId"] = cooperative, ["Code"] = "KV01", ["Name"] = "A", ["StatusId"] = StatusIds.Usage.InUse });
        await InsertAsync(db, "Rafts", new Dictionary<string, object> { ["Id"] = raft, ["ZoneId"] = zone, ["Code"] = "B01", ["PhotoUrl"] = "raft-photos/2026/01/old.jpg", ["StatusId"] = StatusIds.Usage.InUse });
        await InsertAsync(db, "Lanes", new Dictionary<string, object> { ["Id"] = laneOne, ["RaftId"] = raft, ["LaneNumber"] = 1 });
        await InsertAsync(db, "Lanes", new Dictionary<string, object> { ["Id"] = laneTwo, ["RaftId"] = raft, ["LaneNumber"] = 2 });
        await InsertAsync(db, "RaftLayoutVersions", new Dictionary<string, object> { ["RaftId"] = raft, ["VersionNumber"] = 1, ["LaneCount"] = 2 });
        await InsertAsync(db, "Suppliers", new Dictionary<string, object> { ["Id"] = supplier, ["Code"] = "NCC01", ["Name"] = "S", ["PhoneNumber"] = "0900000002", ["SupplierType"] = "SeedSupplier", ["StatusId"] = StatusIds.Usage.InUse });
        await InsertAsync(db, "FarmingInputItems", new Dictionary<string, object> { ["Id"] = seedItem, ["Code"] = "SEED-01", ["Name"] = "Seed", ["Group"] = "Seed", ["Unit"] = "con", ["StatusId"] = StatusIds.Usage.InUse });
        await InsertAsync(db, "FarmingInputItems", new Dictionary<string, object> { ["Id"] = materialItem, ["Code"] = "MAT-01", ["Name"] = "Buoy", ["Group"] = "RaftMaterial", ["Unit"] = "piece", ["StatusId"] = StatusIds.Usage.InUse });
        await InsertAsync(db, "PurchaseReceipts", new Dictionary<string, object>
        {
            ["Id"] = receipt, ["Code"] = "PR-20260110-001", ["SupplierId"] = supplier, ["ZoneId"] = zone, ["RaftId"] = raft, ["ReceiptDate"] = new DateTime(2026, 1, 10), ["RecordedByAccountId"] = account, ["StatusId"] = StatusIds.PurchaseReceipt.Completed,
            ["Subtotal"] = 11_000m, ["DiscountAmount"] = 0m, ["PayableAmount"] = 11_000m,
        });
        await InsertAsync(db, "PurchaseReceiptLines", new Dictionary<string, object> { ["Id"] = seedLine, ["ReceiptId"] = receipt, ["ItemId"] = seedItem, ["Unit"] = "con", ["Quantity"] = 10m, ["UnitPrice"] = 1_000m, ["LineAmount"] = 10_000m });
        await InsertAsync(db, "PurchaseReceiptLines", new Dictionary<string, object> { ["Id"] = materialLine, ["ReceiptId"] = receipt, ["ItemId"] = materialItem, ["Unit"] = "piece", ["Quantity"] = 1m, ["UnitPrice"] = 1_000m, ["LineAmount"] = 1_000m });
        await InsertAsync(db, "SeedReceiptLots", new Dictionary<string, object>
        {
            ["Id"] = lot, ["PurchaseReceiptLineId"] = seedLine, ["SeedTypeId"] = seedItem, ["SupplierId"] = supplier, ["InternalCode"] = "PR-20260110-001-S1", ["ReceivedDate"] = new DateTime(2026, 1, 10),
        });
        await InsertAsync(db, "PurchaseReturns", new Dictionary<string, object> { ["Id"] = returnId, ["Code"] = "PRT-20260112-001", ["ReceiptId"] = receipt, ["ReturnDate"] = new DateTime(2026, 1, 12), ["Reason"] = "Weak", ["RecordedByAccountId"] = account });
        await InsertAsync(db, "PurchaseReturnLines", new Dictionary<string, object> { ["ReturnId"] = returnId, ["ReceiptLineId"] = seedLine, ["Quantity"] = 2m, ["ReturnValue"] = 2_000m });
        await InsertAsync(db, "SupplierPayments", new Dictionary<string, object>
        {
            ["SupplierId"] = supplier, ["ReceiptId"] = receipt, ["Code"] = "PMT-1", ["Amount"] = 3_000m, ["PaymentDate"] = new DateTime(2026, 1, 15), ["Method"] = "Cash", ["DeduplicationKey"] = "k1", ["RecordedByAccountId"] = account, ["StatusId"] = StatusIds.SupplierPayment.Recorded,
        });
        await InsertAsync(db, "CultivationCycles", new Dictionary<string, object> { ["Id"] = cycle, ["RaftId"] = raft, ["OpenedAt"] = at, ["StatusId"] = StatusIds.CultivationCycle.Open });
        var seeding = New();
        await InsertAsync(db, "SeedingEvents", new Dictionary<string, object> { ["Id"] = seeding, ["CycleId"] = cycle, ["SeedLotId"] = lot, ["SeedingDate"] = new DateTime(2026, 3, 11), ["RecordedByAccountId"] = account, ["EvidenceUrls"] = "[]" });
        await InsertAsync(db, "CultivationLogEntries", new Dictionary<string, object> { ["Id"] = entry, ["ZoneId"] = zone, ["CycleId"] = cycle, ["LaneId"] = laneOne, ["OccurredAt"] = at, ["EventType"] = "Observation", ["RecordedByAccountId"] = account, ["EvidenceUrls"] = "[]" });
        await InsertAsync(db, "LaneSegments", new Dictionary<string, object>
        {
            ["CycleId"] = cycle, ["LaneId"] = laneOne, ["LogId"] = entry, ["SeedingId"] = seeding, ["SegmentType"] = "Seeded", ["FromPosition"] = 0m, ["ToPosition"] = 10m, ["SegmentDate"] = new DateTime(2026, 3, 11), ["RecordedByAccountId"] = account,
        });

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());

        // The cycle: its start is the business date it was opened, Open and free for the first session (the old events named no lane).
        var kept = await db.CultivationCycles.AsNoTracking().SingleAsync(c => c.Id == cycle);
        Assert.Equal((new DateOnly(2026, 3, 10), StatusIds.CultivationCycle.Open), (kept.StartDate, kept.StatusId));
        Assert.Equal(0, await db.SeedingRecords.CountAsync());

        // The raft: photo moved, every lane In Use.
        Assert.Equal("raft-photos/2026/01/old.jpg", (await db.RaftPhotos.AsNoTracking().SingleAsync(p => p.RaftId == raft)).FileKey);
        Assert.All(await db.Lanes.AsNoTracking().Where(l => l.RaftId == raft).ToListAsync(), l => Assert.Equal(StatusIds.Lane.InUse, l.StatusId));

        // The receipt: on its lines the old raft and a number, the group renamed, the return and the payment kept.
        var lines = await db.PurchaseReceiptLines.AsNoTracking().Include(l => l.Item).Where(l => l.ReceiptId == receipt).OrderBy(l => l.LineNumber).ToListAsync();
        Assert.Equal([1, 2], lines.Select(l => l.LineNumber));
        Assert.All(lines, l => Assert.Equal(raft, l.RaftId));
        Assert.Equal(InputItemGroup.Material, lines.Single(l => l.Id == materialLine).Item!.Group);
        Assert.Equal(StatusIds.PurchaseReceipt.Completed, (await db.PurchaseReceipts.AsNoTracking().SingleAsync(r => r.Id == receipt)).StatusId);
        Assert.Equal(StatusIds.PurchaseReturn.Recorded, (await db.PurchaseReturns.AsNoTracking().SingleAsync(r => r.Id == returnId)).StatusId);
        Assert.Equal(2m, (await db.PurchaseReturnLines.AsNoTracking().SingleAsync(l => l.ReceiptLineId == seedLine)).Quantity);
        Assert.Equal(StatusIds.SupplierPayment.Recorded, (await db.SupplierPayments.AsNoTracking().SingleAsync(p => p.SupplierId == supplier)).StatusId);

        // The account pairs and seeds of the old model stay usable by the new reads of the seed lot.
        var lotRow = await db.PurchaseReceiptLines.AsNoTracking().Where(l => l.Id == seedLine).Select(l => new
        {
            Received = l.Quantity,
            Seeded = db.SeedingRecords.Where(s => s.SeedLotLineId == l.Id).Sum(s => (decimal?)s.Quantity) ?? 0,
            Returned = db.PurchaseReturnLines.Where(r => r.ReceiptLineId == l.Id && r.Return.StatusId == StatusIds.PurchaseReturn.Recorded).Sum(r => (decimal?)r.Quantity) ?? 0,
        }).SingleAsync();
        Assert.Equal((10m, 0m, 2m), (lotRow.Received!.Value, lotRow.Seeded, lotRow.Returned));
    }
}
