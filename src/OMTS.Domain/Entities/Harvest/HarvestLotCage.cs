using OMTS.Domain.Common;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>
/// One cage of a lot put on the scale at the port (owner decision of 24/11/2026): every harvest trip of an order is weighed cage by cage, and the weighing of the lot is the sum of its cages,
/// per product. Each cage is saved as soon as it is weighed, so nothing is lost when the app closes; completing the weighing gives the lot its weighed kg. A cage can be corrected or removed
/// until the lot is closed, and the lot keeps the kg of its last completed weighing until the weighing is completed again.
/// </summary>
public class HarvestLotCage : Entity
{
    /// <summary>The most cages one lot can hold; a lot is one trip to one raft.</summary>
    public const int MaxCages = 1000;

    public Guid LotId { get; set; }
    public HarvestLot Lot { get; set; } = null!;

    /// <summary>1, 2, … in the order weighed; a removed cage leaves its number unused.</summary>
    public int Number { get; set; }

    /// <summary>The product of the cage, one of the products of the lot's lines; null for a lot with no product lines.</summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Above 0, at most 3 decimals.</summary>
    public decimal Kg { get; set; }

    /// <summary>When the cage was weighed, or last corrected.</summary>
    public DateTimeOffset WeighedAt { get; set; }

    /// <summary>Who weighed the cage, or last corrected it.</summary>
    public Guid WeighedByAccountId { get; set; }

    /// <summary>Made by the app for the cage, so a cage sent again after a lost answer is not weighed twice; null when the app sent none.</summary>
    public Guid? ClientOperationId { get; set; }
}
