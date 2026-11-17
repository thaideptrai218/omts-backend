namespace OMTS.Domain.Enums;

/// <summary>
/// What a purchase receipt holds, fixed when it is created: Seed receipts hold only Seed items (their lines are seed lots), Supply receipts hold Material and Other items and no seed. A receipt never mixes the two,
/// and a purchase return takes the kind of its receipt.
/// </summary>
public enum PurchaseReceiptKind
{
    Seed,
    Supply,
}
