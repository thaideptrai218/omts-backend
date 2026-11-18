namespace OMTS.Domain.Enums;

/// <summary>
/// What an <c>InputStockMovement</c> records. A reversal undoes an earlier movement of the same document when it is edited or cancelled. A cost adjustment records a change of the cost price
/// of the item with no quantity; it is never reversed: whatever changes the cost next writes its own cost adjustment.
/// </summary>
public enum InputStockMovementKind
{
    Opening,
    Receipt,
    PurchaseReturn,
    Issue,
    Disposal,
    Reversal,
    CostAdjustment,
}
