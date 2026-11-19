using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>What the sales side knows of a lot (BR-HARVEST-07): the kg on invoice lines, without cancelled invoices and with corrected ones at their latest kg, and on sales returns that are not cancelled.</summary>
/// <param name="BackForSaleKg">The returned kg that went back to the lot.</param>
/// <param name="DiscardedKg">The returned kg that was thrown away.</param>
public sealed record LotSalesFigures(decimal InvoicedKg, decimal ReturnedKg, decimal BackForSaleKg, decimal DiscardedKg)
{
    public static readonly LotSalesFigures None = new(0m, 0m, 0m, 0m);
}

/// <summary>
/// The figures of a lot (BR-HARVEST-07), worked out each time they are shown, never stored. They count from the weighed kg once the lot is weighed, and from the estimated kg (a cage count, so it need not
/// equal sold plus lost) before. Until the lot is closed they are not final.
/// </summary>
public sealed record HarvestLotFigures(decimal InvoicedKg, decimal ReturnedKg, decimal SoldKg, decimal RemainingKg, decimal LostKg, decimal DifferenceKg, bool NotFinal)
{
    public static HarvestLotFigures Of(HarvestLot lot, LotSalesFigures sales) => Of(lot.EstimatedKg, lot.WeighedKg, lot.LeftoverKg, lot.StatusId != StatusIds.HarvestLot.Closed, sales);

    /// <summary>
    /// The figures a screen shows for a lot. A lot still waiting for its port weighing has nothing to sell and nothing left: its estimate (cages times kg per cage) is never shown as a remaining or a difference,
    /// so a list total never mixes estimates with weighed kg (owner decision 18/11/2026). Closed and old lots keep <see cref="Of(HarvestLot, LotSalesFigures)"/>.
    /// </summary>
    public static HarvestLotFigures Shown(HarvestLot lot, LotSalesFigures sales) =>
        Shown(lot.StatusId, lot.EstimatedKg, lot.WeighedKg, lot.LeftoverKg, sales);

    public static HarvestLotFigures Shown(int statusId, decimal estimatedKg, decimal? weighedKg, decimal? leftoverKg, LotSalesFigures sales)
    {
        var worked = Of(estimatedKg, weighedKg, leftoverKg, statusId != StatusIds.HarvestLot.Closed, sales);
        return statusId == StatusIds.HarvestLot.AwaitingWeighing && weighedKg is null ? worked with { RemainingKg = 0m, DifferenceKg = 0m } : worked;
    }

    /// <summary>The figures from the kg of the lot, for a lot read as a list row.</summary>
    public static HarvestLotFigures Of(decimal estimatedKg, decimal? weighedKg, decimal? leftoverKg, bool notFinal, LotSalesFigures sales)
    {
        var sold = sales.InvoicedKg - sales.ReturnedKg;
        var lost = (leftoverKg ?? 0m) + sales.DiscardedKg;
        var baseKg = weighedKg ?? estimatedKg;
        return new HarvestLotFigures(
            sales.InvoicedKg,
            sales.ReturnedKg,
            sold,
            Math.Max(0m, baseKg - sales.InvoicedKg + sales.BackForSaleKg),
            lost,
            baseKg - sold - lost,
            notFinal);
    }
}
