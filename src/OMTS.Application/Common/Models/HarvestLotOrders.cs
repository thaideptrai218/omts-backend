namespace OMTS.Application.Common.Models;

/// <summary>
/// The orders a lot is harvested for: the orders (not cancelled) its harvest plan serves, and the trader accounts of their customers. A lot outside a plan has none.
/// </summary>
/// <param name="OrderCodes">DH000001, … oldest code first.</param>
/// <param name="Traders">The distinct trader accounts among the customers of those orders; a walk-in order or a customer without an account adds none.</param>
public sealed record HarvestLotOrders(Guid LotId, IReadOnlyList<string> OrderCodes, IReadOnlyList<HarvestLotTrader> Traders)
{
    /// <summary>The trader of the lot (owner decision of 24/11/2026): the one trader account of its orders; none when there is no trader account, or more than one.</summary>
    public HarvestLotTrader? Trader => Traders.Count == 1 ? Traders[0] : null;

    public static HarvestLotOrders None(Guid lotId) => new(lotId, [], []);
}

/// <summary>A trader account a lot is harvested for, with the name of its customer profile.</summary>
public sealed record HarvestLotTrader(Guid AccountId, string Name);
