using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Rows for a migration test that stands on an older schema. The current model writes every column it has, also those a later migration adds, so a table that gained columns since is written by hand.
/// </summary>
public static class OldSchemaRows
{
    /// <summary>A cooperative with the columns every schema since CooperativeProfileV2 has: <c>Cooperatives</c> gains the harvest settings in HarvestLots.</summary>
    public static async Task<Guid> CooperativeAsync(OmtsDbContext db)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({id}, N'HTX', SYSDATETIMEOFFSET())");
        return id;
    }

    /// <summary>
    /// An Open cycle on a raft of a zone, with the columns of every schema before the nursery, the cycle product and the raft size (cycle: <c>NurseryBatchId</c>, <c>ProductId</c>; raft: <c>LengthM</c>,
    /// <c>WidthM</c>, <c>Note</c>). Returns the id of the cycle.
    /// </summary>
    public static async Task<Guid> OpenCycleAsync(OmtsDbContext db, Guid cooperativeId)
    {
        var (zoneId, raftId, cycleId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO FarmingZones (Id, CooperativeId, Code, Name, StatusId, CreatedAt) VALUES ({zoneId}, {cooperativeId}, N'KV01', N'A', {StatusIds.Usage.InUse}, SYSDATETIMEOFFSET());
            INSERT INTO Rafts (Id, ZoneId, Code, Size, StatusId, CreatedAt) VALUES ({raftId}, {zoneId}, N'B01', N'1', {StatusIds.Usage.InUse}, SYSDATETIMEOFFSET());
            INSERT INTO CultivationCycles (Id, RaftId, StartDate, StatusId, ClosedEarly, CreatedAt) VALUES ({cycleId}, {raftId}, '2026-01-01', {StatusIds.CultivationCycle.Open}, 0, SYSDATETIMEOFFSET());
            """);
        return cycleId;
    }

    /// <summary>An input item with the columns of every schema before InputItemUnitsGroupsCostAndStockLedger, which adds the units, the group link, the cost and the hidden flag.</summary>
    public static async Task<Guid> InputItemAsync(OmtsDbContext db, string code, string name, string group, string unit)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO FarmingInputItems (Id, Code, Name, [Group], Unit, StatusId, CreatedAt) VALUES ({id}, {code}, {name}, {group}, {unit}, 101, SYSDATETIMEOFFSET())");
        return id;
    }
    /// <summary>A supplier with the columns of every schema before SupplierProfileFields, which adds the code.</summary>
    public static async Task<Guid> SupplierAsync(OmtsDbContext db)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO Suppliers (Id, Name, SupplierType, PhoneNumber, CertificateUrls, StatusId, CreatedAt) VALUES ({id}, N'S', N'Seed', N'0900000002', N'[]', 101, SYSDATETIMEOFFSET())");
        return id;
    }

    /// <summary>
    /// A completed receipt PN000001 dated 2026-01-01 with one line of 10 at price 1, with the columns of every schema from PurchaseReceiptsV2 up to
    /// PurchaseDiscountsZonesAndDraftReturns, which adds the discount and the zone. Returns the id of the line.
    /// </summary>
    public static async Task<Guid> CompletedReceiptLineAsync(OmtsDbContext db, Guid supplierId, Guid recordedByAccountId, Guid itemId, string unit)
    {
        var (receiptId, lineId) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO PurchaseReceipts (Id, Code, SupplierId, ReceiptDate, RecordedByAccountId, StatusId, CreatedAt)
            VALUES ({receiptId}, N'PN000001', {supplierId}, '2026-01-01', {recordedByAccountId}, {StatusIds.PurchaseReceipt.Completed}, SYSDATETIMEOFFSET());
            INSERT INTO PurchaseReceiptLines (Id, ReceiptId, LineNumber, ItemId, Unit, Quantity, UnitPrice, LineAmount)
            VALUES ({lineId}, {receiptId}, 1, {itemId}, {unit}, 10, 1, 10);
            """);
        return lineId;
    }
}
