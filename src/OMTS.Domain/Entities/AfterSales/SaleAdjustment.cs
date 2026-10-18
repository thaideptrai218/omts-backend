using OMTS.Domain.Common;
using OMTS.Domain.Enums;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>
/// E32 – Post-sale discount or weight-correction increase/decrease; never changes the original price.
/// Only Approved adjustments affect debt; totals never exceed the sale value (BR-16, BR-24).
/// </summary>
public class SaleAdjustment : AuditableEntity, IHasStatus
{
    public Guid SaleId { get; set; }
    public SaleTransaction Sale { get; set; } = null!;

    public Guid? ClaimId { get; set; }
    public QualityClaim? Claim { get; set; }

    /// <summary>New weighing slip for a weight correction (BR-23).</summary>
    public Guid? NewWeighingId { get; set; }
    public PortWeighing? NewWeighing { get; set; }

    public AdjustmentType AdjustmentType { get; set; }
    public AdjustmentMode Mode { get; set; }
    public decimal? Percentage { get; set; }

    /// <summary>Effective amount, VND, multiple of 1,000.</summary>
    public decimal Amount { get; set; }

    public string? NegotiationBasis { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];

    public string? Decision { get; set; }
    public Guid? DecidedByAccountId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? ApprovedByAccountId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public int StatusId { get; set; } = StatusIds.SaleAdjustment.Draft;
    public Status Status { get; set; } = null!;
}
