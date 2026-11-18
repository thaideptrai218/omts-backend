namespace OMTS.Application.Common.Interfaces;

/// <summary>
/// One material usage of a raft with the price it is costed at: the cost price of its item per base unit, as the Xuất dùng
/// document of the usage booked it. <see cref="UnitPrice"/> is the cost snapshot on the issue line of the item when the usage has
/// an issue; for a usage without one (made before stock was checked) it is the cost of the latest ledger movement of the item up
/// to the end of the usage day (UTC+7). It is null when the item had no cost then.
/// </summary>
public sealed record RaftUsageCostRow(
    Guid RaftId, string RaftCode, Guid ZoneId, string ZoneCode, string ZoneName,
    Guid UsageId, DateOnly UsageDate, string RepairReason, Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal? UnitPrice);

/// <summary>Which usages to cost; null means any. The period is on the usage date, the zones are those of the rafts.</summary>
public sealed record RaftUsageCostFilter(
    DateOnly? From = null, DateOnly? To = null, IReadOnlyCollection<Guid>? RaftIds = null, Guid? ItemId = null, IReadOnlyCollection<Guid>? ZoneIds = null);

/// <summary>
/// Material cost of rafts (FARM-04, BR-FARM-05) = Σ of the material used on them × the cost price of each item
/// (<see cref="RaftUsageCostRow"/>), half up to the dong per usage. A usage of an item with no cost costs 0.
/// </summary>
public interface IRaftMaterialCostQuery
{
    /// <summary>VND per raft id over every usage it has, 0 for a raft without any.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetTotalsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken);

    /// <summary>The matching usages with their prices, in no particular order.</summary>
    Task<IReadOnlyList<RaftUsageCostRow>> UsagesAsync(RaftUsageCostFilter filter, CancellationToken cancellationToken);
}
