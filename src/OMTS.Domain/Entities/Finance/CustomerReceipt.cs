using OMTS.Domain.Common;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Finance;

/// <summary>
/// E34 – Money actually received from a customer, including advance payments (BR-13, BR-24).
/// The reference code is unique per method. Cooperative-wide data.
/// </summary>
public class CustomerReceipt : AuditableEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>VND, multiple of 1,000.</summary>
    public decimal Amount { get; set; }

    public DateOnly ReceiptDate { get; set; }
    public PaymentMethod Method { get; set; }
    public string? ReferenceCode { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];

    public string? DeduplicationKey { get; set; }

    public ICollection<ReceiptAllocation> Allocations { get; set; } = [];
}
