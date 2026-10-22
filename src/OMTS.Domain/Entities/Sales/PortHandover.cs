using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>
/// E27 – Proof the current buyer received the goods at the port. Allowed only while the lot's
/// publication is Published; an offline handover fills the publication id after synchronization.
/// </summary>
public class PortHandover : AuditableEntity
{
    public Guid OrderId { get; set; }
    public SalesOrder Order { get; set; } = null!;

    public Guid WeighingId { get; set; }
    public PortWeighing Weighing { get; set; } = null!;

    public Guid? PublicationIdAtHandover { get; set; }
    public TracePublicationVersion? PublicationAtHandover { get; set; }

    public string? DeliveredBy { get; set; }
    public string? ReceivedBy { get; set; }
    public DateTimeOffset HandedOverAt { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];

    /// <summary>Identity/contacts of the recipient and confirmer at handover (JSON snapshot).</summary>
    public string? RecipientSnapshot { get; set; }

    public bool IsOffline { get; set; }
    public DateTimeOffset? SynchronizedAt { get; set; }
}
