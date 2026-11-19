using OMTS.Application.Features.Purchasing.SeedLots;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// The seed lot register filters; null means "any". The period is on the date the receipt was received. <paramref name="ZoneId"/> keeps the lots used in that zone (a nursery batch of the zone, a seeding on a raft of
/// the zone) or received for it. <paramref name="Search"/> is text in the receipt code, the seed lot code (PN000123-1), the supplier's lot code or the code or name of the seed item.
/// </summary>
public sealed record SeedLotRegisterFilter(DateOnly? From = null, DateOnly? To = null, Guid? SupplierId = null, Guid? ItemId = null, Guid? ZoneId = null, string? Search = null);

/// <summary>A seed lot (a Seed line of a Completed receipt) with its supplier, seed item, the product the item sells as, and its quantities.</summary>
public sealed record SeedLotFiguresRow(
    Guid LineId,
    int LineNumber,
    Guid ReceiptId,
    string ReceiptCode,
    DateTimeOffset ReceivedAt,
    Guid SupplierId,
    string SupplierCode,
    string SupplierName,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    Guid? ProductId,
    string? ProductCode,
    string? ProductName,
    string UnitName,
    SeedLotFigures Figures);

public sealed record SeedLotNurseryBatchRow(Guid Id, string Code, int StatusId, decimal QuantityIn, decimal? Survivors, DateOnly StartDate);

public sealed record SeedLotSeedingRow(Guid Id, Guid CycleId, string RaftCode, string ZoneCode, decimal Quantity, DateOnly SeedingDate);

public sealed record SeedLotReturnRow(Guid Id, string Code, decimal Quantity, DateTimeOffset ReturnedAt, int StatusId);

public sealed record SeedLotIssueRow(Guid Id, string Code, InputIssueKind Kind, decimal Quantity, DateTimeOffset IssuedAt);

public sealed record SeedLotCycleRow(Guid Id, string RaftCode, string ZoneCode, int StatusId, string? ProductCode);

public sealed record SeedLotHarvestLotRow(Guid Id, string LotCode, int StatusId, decimal? WeighedKg, decimal EstimatedKg);

/// <summary>What hangs off one seed lot: its nursery batches, seeding sessions, returns, manual issues, the cycles it was hung in and their harvest lots.</summary>
public sealed record SeedLotDetailRows(
    IReadOnlyList<SeedLotNurseryBatchRow> NurseryBatches,
    IReadOnlyList<SeedLotSeedingRow> Seedings,
    IReadOnlyList<SeedLotReturnRow> Returns,
    IReadOnlyList<SeedLotIssueRow> Issues,
    IReadOnlyList<SeedLotCycleRow> Cycles,
    IReadOnlyList<SeedLotHarvestLotRow> HarvestLots);

/// <summary>One seeding session total: the pieces of a seed lot hung in a cycle (every lot of the cycle is listed, so a cycle's whole seeding is known).</summary>
public sealed record SeedLotCycleSeeding(Guid CycleId, Guid SeedLotLineId, decimal Quantity);

/// <summary>A harvest lot of a cycle with its kg; the sales figures are read separately by lot id.</summary>
public sealed record SeedLotCycleHarvest(Guid CycleId, Guid HarvestLotId, decimal EstimatedKg, decimal? WeighedKg);

/// <summary>What the harvest and sales figures of seed lots are worked out from: the seeding of every cycle the lots were hung in, and the harvest lots of those cycles.</summary>
public sealed record SeedLotHarvestSources(IReadOnlyList<SeedLotCycleSeeding> Seedings, IReadOnlyList<SeedLotCycleHarvest> HarvestLots)
{
    public static readonly SeedLotHarvestSources Empty = new([], []);
}

/// <summary>What the seed lot screen reads: the lots with their quantities worked out by the database (a few set queries, no query per lot).</summary>
public interface ISeedLotRegisterQuery
{
    /// <summary>Every lot the filters match, newest receipt first; the state, sort and page are applied by the caller over the figures.</summary>
    Task<IReadOnlyList<SeedLotFiguresRow>> SearchAsync(SeedLotRegisterFilter filter, CancellationToken cancellationToken);

    Task<SeedLotFiguresRow?> GetAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>The quantities of these lots (receipt lines); a line that is not a seed lot of a Completed receipt is absent.</summary>
    Task<IReadOnlyDictionary<Guid, SeedLotFigures>> FiguresAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken);

    /// <summary>The quantities of the seed lots of these receipts, per receipt; a receipt that is not Completed or has no Seed line is absent.</summary>
    Task<ILookup<Guid, SeedLotFigures>> FiguresOfReceiptsAsync(IReadOnlyCollection<Guid> receiptIds, CancellationToken cancellationToken);

    Task<SeedLotDetailRows> DetailAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>The seeding of every cycle these lots were hung in (all lots of those cycles) and the harvest lots of those cycles: two set queries, whatever the number of lots.</summary>
    Task<SeedLotHarvestSources> HarvestSourcesAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken);
}
