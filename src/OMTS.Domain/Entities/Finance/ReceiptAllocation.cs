using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>E35 – Part of a receipt allocated to a sale; total allocated ≤ amount received.</summary>
public class ReceiptAllocation : AuditableEntity
{
    public Guid ReceiptId { get; set; }
    public CustomerReceipt Receipt { get; set; } = null!;

    public Guid SaleId { get; set; }
    public SaleTransaction Sale { get; set; } = null!;

    /// <summary>VND, multiple of 1,000.</summary>
    public decimal AllocatedAmount { get; set; }
}
