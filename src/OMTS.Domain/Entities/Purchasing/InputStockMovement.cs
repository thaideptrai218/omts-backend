using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// One row of the append-only movement ledger of an input item, in base units. The quantity on hand of an item is the sum of its rows, that of a seed lot the sum of the
/// rows naming the lot. A document that is edited or cancelled never changes its rows: a <see cref="InputStockMovementKind.Reversal"/> row with the opposite quantity
/// (naming the row it undoes in <see cref="ReversesMovementId"/>) is added, then the new rows.
/// </summary>
public class InputStockMovement : Entity
{
    /// <summary>Write order; breaks ties between rows with the same time.</summary>
    public long Sequence { get; set; }

    public Guid ItemId { get; set; }
    public FarmingInputItem Item { get; set; } = null!;

    /// <summary>The seed lot (receipt line) the quantity belongs to, on seed rows of a receipt.</summary>
    public Guid? SeedLotLineId { get; set; }

    public InputStockMovementKind Kind { get; set; }

    /// <summary>The receipt, return or issue; null on an opening row.</summary>
    public Guid? DocumentId { get; set; }
    public string DocumentCode { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Signed: positive adds to the stock.</summary>
    public decimal Quantity { get; set; }

    /// <summary>The cost of one base unit of the item when the row was written.</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>The price of one base unit on the document (a receipt or return price); null on rows with none.</summary>
    public decimal? TransactionPrice { get; set; }

    /// <summary>The supplier of a receipt or return, or zone · raft · receiver of an issue.</summary>
    public string? PartyName { get; set; }

    public Guid? ReversesMovementId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedByAccountId { get; set; }
}
