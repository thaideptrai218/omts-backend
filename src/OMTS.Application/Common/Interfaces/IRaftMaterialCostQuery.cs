namespace OMTS.Application.Common.Interfaces;

/// <summary>
/// One material usage of a raft with the price it is costed at: the unit price less the line discount of the latest Completed
/// purchase receipt line of the item received on or before the usage date (latest time received first). <see cref="UnitPrice"/>
/// is null when the item had not been bought by then.
/// </summary>
public sealed record RaftUsageCostRow(
    Guid RaftId, string RaftCode, Guid ZoneId, string ZoneCode, string ZoneName,
    Guid UsageId, DateOnly UsageDate, string RepairReason, Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal? UnitPrice);

/// <summary>Which usages to cost; null means any. The period is on the usage date, the zones are those of the rafts.</summary>
public sealed record RaftUsageCostFilter(
    DateOnly? From = null, DateOnly? To = null, IReadOnlyCollection<Guid>? RaftIds = null, Guid? ItemId = null, IReadOnlyCollection<Guid>? ZoneIds = null);

/// <summary>
/// Material cost of rafts (FARM-04, BR-FARM-05) = Σ of the material used on them × the latest purchase price of each item
/// (<see cref="RaftUsageCostRow"/>), half up to the dong per usage. A usage of an item never bought costs 0.
/// </summary>
public interface IRaftMaterialCostQuery
{
    /// <summary>VND per raft id over every usage it has, 0 for a raft without any.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetTotalsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken);

    /// <summary>The matching usages with their prices, in no particular order.</summary>
    Task<IReadOnlyList<RaftUsageCostRow>> UsagesAsync(RaftUsageCostFilter filter, CancellationToken cancellationToken);
}
