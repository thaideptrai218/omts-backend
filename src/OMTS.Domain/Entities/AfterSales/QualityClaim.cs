using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.AfterSales;

/// <summary>
/// E31 – Complaint awaiting decision; by itself it neither reduces debt nor means goods came back (BR-15).
/// The order and lot are derived from the sale.
/// </summary>
public class QualityClaim : AuditableEntity, IHasStatus
{
    public Guid SaleId { get; set; }
    public SaleTransaction Sale { get; set; } = null!;

    public string? AffectedPortion { get; set; }
    public string ComplaintType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ClaimHandling RequestedHandling { get; set; }

    public RequestChannel ReceivingChannel { get; set; }
    public Guid? RecordedByAccountId { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];

    /// <summary>Separate from the status; AcceptReturn only when the claim is a return request.</summary>
    public ClaimDecision? Decision { get; set; }
    public Guid? DecidedByAccountId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public int StatusId { get; set; } = StatusIds.QualityClaim.Pending;
    public Status Status { get; set; } = null!;
}
