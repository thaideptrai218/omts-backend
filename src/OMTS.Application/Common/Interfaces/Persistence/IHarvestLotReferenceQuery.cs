namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A seed lot (a Seed line of a purchase receipt) that went into a cycle, with the item it is.</summary>
public sealed record LotSeedLotRow(Guid ReceiptId, string ReceiptCode, int LineNumber, string ItemCode, string ItemName);

/// <summary>A nursery batch the cycle was started from.</summary>
public sealed record LotNurseryBatchRow(Guid Id, string Code);

/// <summary>The seed lots of a cycle and the nursery batch it came from, if any.</summary>
public sealed record LotOriginRows(IReadOnlyList<LotSeedLotRow> SeedLots, LotNurseryBatchRow? NurseryBatch);

/// <summary>A sales invoice with the order it was made from, if any.</summary>
public sealed record LotInvoiceRow(Guid Id, string Code, Guid? SalesOrderId, string? SalesOrderCode);

/// <summary>What the harvest lot screen links to besides the lot itself: where its seed came from and the sales documents that touched it. Read only, a fixed number of queries.</summary>
public interface IHarvestLotReferenceQuery
{
    /// <summary>
    /// The seed lots of the cycle: the receipt lines of its seeding records and of its nursery batch, each once, by receipt code then line number; and the nursery batch itself
    /// (<paramref name="nurseryBatchId"/> is <c>CultivationCycle.NurseryBatchId</c>).
    /// </summary>
    Task<LotOriginRows> OriginOfCycleAsync(Guid cycleId, Guid? nurseryBatchId, CancellationToken cancellationToken);

    /// <summary>The invoices with their orders, by invoice id; an id that is no invoice is absent.</summary>
    Task<IReadOnlyDictionary<Guid, LotInvoiceRow>> InvoicesAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken);
}
