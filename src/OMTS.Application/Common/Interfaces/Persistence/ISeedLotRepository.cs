using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A seed lot on the Seeding screen (SCR-CYCLE-02). It holds no price.</summary>
public sealed record SeedLotRow(
    Guid LineId,
    int LineNumber,
    Guid ReceiptId,
    string ReceiptCode,
    DateOnly ReceivedDate,
    Guid? ItemId,
    string? ItemCode,
    string? ItemName,
    string Unit,
    Guid SupplierId,
    string SupplierName,
    Guid? RaftId,
    decimal QuantityReceived,
    decimal QuantitySeeded,
    decimal QuantityReturned = 0m,
    decimal QuantityConsumed = 0m);

/// <summary>The seed lots seeding can choose from (GBR-04): the Seed lines of Completed purchase receipts.</summary>
public interface ISeedLotRepository
{
    /// <summary>The receipt line with its receipt, supplier and item, tracked: a session saves with the receipt row.</summary>
    Task<PurchaseReceiptLine?> GetForSeedingAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>The quantity of the line on purchase returns that are not cancelled.</summary>
    Task<decimal> ReturnedAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>Those bought for this raft first, then the others, the newest first. A lot whose whole quantity has been returned is left out (GBR-04).</summary>
    Task<IReadOnlyList<SeedLotRow>> ListOfferedAsync(Guid raftId, CancellationToken cancellationToken);
}

/// <summary>What PURCHASE reads of seeding (GBR-04): the quantity seeded from each seed lot, the total of its sessions.</summary>
public interface ISeedLotUsageQuery
{
    /// <summary>Per seed lot (receipt line) with at least one session: the total quantity of its sessions.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> SeededAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken);

    /// <summary>The earliest seeding date of any of these seed lots, or null when none was seeded.</summary>
    Task<DateOnly?> FirstSeedingDateAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken);
}
