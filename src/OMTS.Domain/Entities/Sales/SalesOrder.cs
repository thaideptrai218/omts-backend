using OMTS.Domain.Common;
using OMTS.Domain.Enums;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>
/// E23 – Trader order; it exists before any harvest or resale (BR-08). A first-sale order gets at most
/// one new lot; a resale order references an old lot and, for returned goods, the source return.
/// Order edit history is written to AuditEvent.
/// </summary>
public class SalesOrder : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>Order zone; limits sales permissions by zone.</summary>
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    public GoodsSource GoodsSource { get; set; } = GoodsSource.FirstSale;

    /// <summary>Resale order: source lot/QR.</summary>
    public Guid? SourceLotId { get; set; }
    public TraceabilityLot? SourceLot { get; set; }

    /// <summary>Resale order from returned goods.</summary>
    public Guid? SourceReturnId { get; set; }
    public PhysicalReturn? SourceReturn { get; set; }
    public decimal? QuantityAllocatedFromReturn { get; set; }

    /// <summary>Quantity requested by the trader, in kg.</summary>
    public decimal RequestedQuantity { get; set; }
    public DateTimeOffset? RequiredAt { get; set; }
    public string? OriginalTerms { get; set; }

    public RequestChannel RequestChannel { get; set; }
    public string? ChannelEvidence { get; set; }
    public Guid? RecordedByAccountId { get; set; }

    /// <summary>Changes the trader proposed after submission (UC43 branch 3a).</summary>
    public string? TraderProposedChanges { get; set; }

    /// <summary>Channel/time/basis of the trader's agreement to the cooperative's proposal (UC44).</summary>
    public string? AgreementChannel { get; set; }
    public DateTimeOffset? AgreedAt { get; set; }
    public string? AgreementBasis { get; set; }

    public int StatusId { get; set; } = StatusIds.SalesOrder.PendingReview;
    public Status Status { get; set; } = null!;

    public ICollection<LaneSegment> PlannedHarvestSegments { get; set; } = [];
    public ICollection<HarvestEvent> HarvestEvents { get; set; } = [];
    public ICollection<PortWeighing> Weighings { get; set; } = [];
    public ICollection<PortHandover> Handovers { get; set; } = [];
    public SaleTransaction? Sale { get; set; }
}
