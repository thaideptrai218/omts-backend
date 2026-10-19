using OMTS.Domain.Common;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Finance;

/// <summary>E37 – Money actually paid back to a customer, per installment (BR-15/16).</summary>
public class CustomerRefund : AuditableEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public Guid? CreditId { get; set; }
    public CustomerCredit? Credit { get; set; }

    public string Recipient { get; set; } = string.Empty;

    /// <summary>VND, multiple of 1,000.</summary>
    public decimal AmountPaid { get; set; }

    public DateOnly RefundDate { get; set; }
    public PaymentMethod Method { get; set; }
    public string? TransferReference { get; set; }
    public List<string> DocumentUrls { get; set; } = [];

    public string? DeduplicationKey { get; set; }
}
