using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>
/// E25 – Lot/QR formed by one first-sale order (BR-09); reused across later sales through returns
/// (BR-18). No stored status: the condition comes from the current publication version.
/// </summary>
public class TraceabilityLot : AuditableEntity
{
    public string LotCode { get; set; } = string.Empty;

    /// <summary>Immutable token printed in the QR.</summary>
    public string QrCode { get; set; } = string.Empty;

    /// <summary>Changes only when a cancelled order's lot is taken over by another order (UC44 branch 5a2).</summary>
    public Guid InitialOrderId { get; set; }
    public SalesOrder InitialOrder { get; set; } = null!;

    /// <summary>Other disposal when the order is cancelled and the lot is not linked to another order.</summary>
    public string? DisposalMethod { get; set; }
    public string? DisposalReason { get; set; }
    public Guid? DisposedByAccountId { get; set; }
    public DateTimeOffset? DisposedAt { get; set; }

    public ICollection<LaneSegment> SourceSegments { get; set; } = [];
    public ICollection<TracePublicationVersion> PublicationVersions { get; set; } = [];
    public ICollection<TraceCorrection> Corrections { get; set; } = [];
    public ICollection<SaleTransaction> Sales { get; set; } = [];
}
