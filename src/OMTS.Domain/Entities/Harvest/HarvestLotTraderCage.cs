using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>
/// One cage as the trader of the lot weighed it on their own phone, beside the cooperative (owner decision of 24/11/2026): both weigh every cage, the two lists are compared in the order weighed
/// (the i-th cage of each side), and the cooperative's cages stay the official weighing. The trader's cages change no kg of the lot and nothing on the ledger; they only show where the two sides differ.
/// </summary>
public class HarvestLotTraderCage : Entity
{
    public Guid LotId { get; set; }
    public HarvestLot Lot { get; set; } = null!;

    /// <summary>1, 2, … in the order weighed; a removed cage leaves its number unused.</summary>
    public int Number { get; set; }

    /// <summary>Above 0, at most 3 decimals.</summary>
    public decimal Kg { get; set; }

    /// <summary>When the cage was weighed, or last corrected.</summary>
    public DateTimeOffset WeighedAt { get; set; }

    /// <summary>The trader account that weighed the cage, or last corrected it.</summary>
    public Guid WeighedByAccountId { get; set; }

    /// <summary>Made by the app for the cage, so a cage sent again after a lost answer is not weighed twice; null when the app sent none.</summary>
    public Guid? ClientOperationId { get; set; }
}
