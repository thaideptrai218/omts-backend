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
/// The figures of a lot (BR-HARVEST-07), worked out each time they are shown, never stored. The estimated kg comes from a cage count, so it need not equal sold plus lost.
/// While the lot is for sale they are not final.
/// </summary>
public sealed record HarvestLotFigures(decimal InvoicedKg, decimal ReturnedKg, decimal SoldKg, decimal RemainingKg, decimal LostKg, decimal DifferenceKg, bool NotFinal)
{
    public static HarvestLotFigures Of(HarvestLot lot, LotSalesFigures sales)
    {
        var sold = sales.InvoicedKg - sales.ReturnedKg;
        var lost = (lot.LeftoverKg ?? 0m) + sales.DiscardedKg;
        return new HarvestLotFigures(
            sales.InvoicedKg,
            sales.ReturnedKg,
            sold,
            Math.Max(0m, lot.EstimatedKg - sales.InvoicedKg + sales.BackForSaleKg),
            lost,
            lot.EstimatedKg - sold - lost,
            lot.IsForSale);
    }
}
