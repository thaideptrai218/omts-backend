using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.SeedLots;

/// <summary>The seed lot list query. The period is on the date the receipt was received.</summary>
/// <param name="State">One state, or several separated by commas (Unused, Nursing, PartlySeeded, FullySeeded, FullyReturned, FullyLost, UsedUp); left out: every state.</param>
/// <param name="Search">Text in the receipt code, the seed lot code (PN000123-1), the supplier's lot code or the code or name of the seed item.</param>
/// <param name="Sort"><c>code</c> (the default, by receipt code then line number), <c>receivedAt</c> or <c>remaining</c>.</param>
/// <param name="SortDir"><c>asc</c> or <c>desc</c> (the default).</param>
public sealed record SeedLotListQuery(
    DateOnly? From = null, DateOnly? To = null, Guid? SupplierId = null, Guid? ItemId = null, Guid? ZoneId = null, string? State = null, string? Search = null,
    int Page = 1, int PageSize = 20, string? Sort = null, string? SortDir = null);

public sealed record SeedLotNamedRef(Guid Id, string Code, string Name);

/// <summary>
/// One seed lot: a Seed line of a Completed receipt. <paramref name="Id"/> is the receipt line id. Every quantity is in the base unit <paramref name="UnitName"/> of the seed item.
/// <paramref name="NursingQty"/> is what the nursery holds now (batches Nursing and the pieces left in the pool of Completed ones); <paramref name="NurseryLossQty"/> what was lost in the nursery;
/// <paramref name="SeededQty"/> what was hung on lanes, directly or from the nursery; <paramref name="IssuedOtherQty"/> what manual issues and disposals took;
/// <paramref name="RemainingQty"/> = received − returned − seeded − nursery loss − issued, in the nursery or in the stock; <paramref name="AvailableQty"/> is the part of it still in the stock.
/// <paramref name="HarvestedKg"/>, <paramref name="SoldKg"/> (invoiced less returned, cancelled invoices ignored) and <paramref name="ReturnedFromSaleKg"/> are the kg the cycles the lot was hung in gave, shared in proportion to the pieces
/// of each lot when a cycle has several lots; <paramref name="FiguresEstimated"/> is true then, or while a harvest lot is not weighed (its estimated kg counts). They are filled on the list page and the detail, not in the export.
/// <paramref name="State"/> is worked out from these and never stored (the screen no longer shows it). <paramref name="LastUsedAt"/> is the latest nursery start, seeding or issue (a day when only its date is known), null while unused.
/// </summary>
public record SeedLotRegisterRow(
    Guid Id,
    string SeedLotCode,
    Guid ReceiptId,
    string ReceiptCode,
    DateTimeOffset ReceivedAt,
    SeedLotNamedRef Supplier,
    SeedLotNamedRef Item,
    SeedLotNamedRef? Product,
    string UnitName,
    decimal ReceivedQty,
    decimal ReturnedQty,
    decimal NursingQty,
    decimal NurseryLossQty,
    decimal SeededQty,
    decimal IssuedOtherQty,
    decimal RemainingQty,
    SeedLotState State,
    DateTimeOffset? LastUsedAt,
    decimal AvailableQty = 0m,
    decimal HarvestedKg = 0m,
    decimal SoldKg = 0m,
    decimal ReturnedFromSaleKg = 0m,
    bool FiguresEstimated = false);

/// <summary>The sums of every lot the filters match (state filter included), not only the page.</summary>
public sealed record SeedLotRegisterTotals(
    decimal ReceivedQty, decimal ReturnedQty, decimal NursingQty, decimal NurseryLossQty, decimal SeededQty, decimal IssuedOtherQty, decimal RemainingQty);

/// <param name="StateCounts">How many lots each state holds under the other filters (the state filter left out), for the state pills; every state is present.</param>
public sealed record SeedLotRegisterPage(
    IReadOnlyList<SeedLotRegisterRow> Items, int Total, int Page, int PageSize, SeedLotRegisterTotals Totals, IReadOnlyDictionary<SeedLotState, int> StateCounts);

public sealed record SeedLotNurseryBatchView(Guid Id, string Code, int StatusId, string Status, decimal QuantityIn, decimal? Survivors, DateOnly StartDate);

/// <param name="SeededAt">The day of the session at the start of that day (UTC+7): a session records a date, not a time.</param>
public sealed record SeedLotSeedingView(Guid Id, Guid CycleId, string RaftCode, string ZoneCode, decimal Quantity, DateTimeOffset SeededAt);

public sealed record SeedLotReturnView(Guid Id, string Code, decimal Quantity, DateTimeOffset ReturnedAt, int StatusId);

/// <param name="Kind">Use or Disposal; only Completed manual issues are listed (a seeding or nursery issue belongs to its own record).</param>
public sealed record SeedLotIssueView(Guid Id, string Code, InputIssueKind Kind, decimal Quantity, DateTimeOffset IssuedAt);

public sealed record SeedLotCycleView(Guid Id, string RaftCode, string ZoneCode, int StatusId, string? ProductCode);

public sealed record SeedLotHarvestLotView(Guid Id, string LotCode, int StatusId, decimal? WeighedKg, decimal EstimatedKg);

/// <summary>The row with what hangs off the lot, each list oldest first.</summary>
public sealed record SeedLotDetail : SeedLotRegisterRow
{
    public SeedLotDetail(
        SeedLotRegisterRow row,
        IReadOnlyList<SeedLotNurseryBatchView> nurseryBatches,
        IReadOnlyList<SeedLotSeedingView> seedings,
        IReadOnlyList<SeedLotReturnView> returns,
        IReadOnlyList<SeedLotIssueView> issues,
        IReadOnlyList<SeedLotCycleView> cycles,
        IReadOnlyList<SeedLotHarvestLotView> harvestLots)
        : base(row)
    {
        NurseryBatches = nurseryBatches;
        Seedings = seedings;
        Returns = returns;
        Issues = issues;
        Cycles = cycles;
        HarvestLots = harvestLots;
    }

    public IReadOnlyList<SeedLotNurseryBatchView> NurseryBatches { get; init; }

    public IReadOnlyList<SeedLotSeedingView> Seedings { get; init; }

    public IReadOnlyList<SeedLotReturnView> Returns { get; init; }

    public IReadOnlyList<SeedLotIssueView> Issues { get; init; }

    public IReadOnlyList<SeedLotCycleView> Cycles { get; init; }

    public IReadOnlyList<SeedLotHarvestLotView> HarvestLots { get; init; }
}
