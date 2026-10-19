using OMTS.Domain.Common;
using OMTS.Domain.Entities.AfterSales;
using OMTS.Domain.Entities.Finance;
using OMTS.Domain.Entities.Traceability;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// E28 – Sale value finalized after weighing with the agreed price; at most one per order.
/// Amounts are multiples of 1,000 VND (BR-13). The deduplication key prevents duplicates (NFR-04).
/// </summary>
public class SaleTransaction : AuditableEntity
{
    public string Code { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public SalesOrder Order { get; set; } = null!;

    public Guid? LotId { get; set; }
    public TraceabilityLot? Lot { get; set; }

    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public Guid WeighingId { get; set; }
    public PortWeighing Weighing { get; set; } = null!;

    /// <summary>Agreed unit price, VND per kg.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Sale value, VND.</summary>
    public decimal TotalAmount { get; set; }

    public DateTimeOffset FinalizedAt { get; set; }
    public string? SalesReceiptUrl { get; set; }

    /// <summary>Cooperative Owner or Zone Manager in charge of the order zone.</summary>
    public Guid PriceAgreedByAccountId { get; set; }
    public string? AgreementBasis { get; set; }

    public string? DeduplicationKey { get; set; }
    public bool IsOfflineFinalized { get; set; }

    public ICollection<ReceiptAllocation> ReceiptAllocations { get; set; } = [];
    public ICollection<SaleAdjustment> Adjustments { get; set; } = [];
    public ICollection<QualityClaim> Claims { get; set; } = [];
}
