using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Traceability;

/// <summary>
/// E29 – Immutable public version of a lot's traceability record. A new version never overwrites an
/// old one; buyer, price and debt are never exposed (NFR-03).
/// </summary>
public class TracePublicationVersion : AuditableEntity, IHasStatus
{
    public Guid LotId { get; set; }
    public TraceabilityLot Lot { get; set; } = null!;

    public int VersionNumber { get; set; }

    /// <summary>Public content snapshot (JSON, SCR-24 fields only).</summary>
    public string PublicContent { get; set; } = string.Empty;

    /// <summary>Cooperative name/address at publication time (JSON); later UC09 edits don't change it.</summary>
    public string? CooperativeSnapshot { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }

    public bool? ReviewPassed { get; set; }
    public Guid? ReviewedByAccountId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewErrors { get; set; }
    public List<string> ReviewEvidenceUrls { get; set; } = [];

    public Guid? PublishedByAccountId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public Guid? WithdrawnByAccountId { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
    public string? WithdrawalReason { get; set; }

    public int StatusId { get; set; } = StatusIds.TracePublication.Draft;
    public Status Status { get; set; } = null!;
}
