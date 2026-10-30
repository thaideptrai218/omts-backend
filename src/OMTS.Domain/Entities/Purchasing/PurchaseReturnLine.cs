using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

public class PurchaseReturnLine : Entity
{
    public Guid ReturnId { get; set; }
    public PurchaseReturn Return { get; set; } = null!;
    public Guid ReceiptLineId { get; set; }
    public PurchaseReceiptLine ReceiptLine { get; set; } = null!;
    public decimal Quantity { get; set; }
    /// <summary>Quantity × unit price × (total payable ÷ subtotal), rounded to the nearest 1,000 VND.</summary>
    public decimal ReturnValue { get; set; }
}
