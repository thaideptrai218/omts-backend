namespace OMTS.Domain.Enums;

/// <summary>What an <c>InputStockMovement</c> records. A reversal undoes an earlier movement of the same document when it is edited or cancelled.</summary>
public enum InputStockMovementKind
{
    Opening,
    Receipt,
    PurchaseReturn,
    Issue,
    Disposal,
    Reversal,
}
