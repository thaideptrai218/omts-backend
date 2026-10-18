using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>
/// E36 – Credit from an approved discount, a return decision or an unallocated advance payment.
/// No stored status: remaining/used up/refunded is derived from the balance.
/// </summary>
public class CustomerCredit : AuditableEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public Guid? SourceSaleId { get; set; }
    public SaleTransaction? SourceSale { get; set; }

    public Guid? SourceAdjustmentId { get; set; }
    public SaleAdjustment? SourceAdjustment { get; set; }

    /// <summary>Unallocated advance payment.</summary>
    public Guid? SourceReceiptId { get; set; }
    public CustomerReceipt? SourceReceipt { get; set; }

    /// <summary>VND, multiple of 1,000.</summary>
    public decimal Amount { get; set; }

    public ICollection<CustomerRefund> Refunds { get; set; } = [];
}
