using OMTS.Domain.Common;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>
/// The share of a harvest lot that is one product: cages and kg. The lot's own cage count and estimated kg are the sums of its lines, and each line writes one Harvest row on the product ledger.
/// A lot recorded before products were split has no lines and adds nothing to any stock.
/// </summary>
public class HarvestLotProductLine : Entity
{
    public Guid LotId { get; set; }
    public HarvestLot Lot { get; set; } = null!;
    public int LineNumber { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>Above 0.</summary>
    public int CageCount { get; set; }

    /// <summary>Above 0, at most 3 decimals: what was typed, or the cages times the kg of a cage of the product.</summary>
    public decimal Kg { get; set; }
}
