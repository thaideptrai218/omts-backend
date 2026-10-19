using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Traceability;

/// <summary>E30 – Correction of a lot's source/weight data with reason and publication impact (UC39).</summary>
public class TraceCorrection : AuditableEntity
{
    public Guid LotId { get; set; }
    public TraceabilityLot Lot { get; set; } = null!;

    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> EvidenceUrls { get; set; } = [];

    public Guid CorrectedByAccountId { get; set; }
    public Guid? ApprovedByAccountId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public string? PublicationImpact { get; set; }
}
