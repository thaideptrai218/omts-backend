using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Platform;

/// <summary>
/// E48 – Who changed what, when and why (NFR-02). Never edited or deleted. Also the history of every
/// group A status transition, so there is no separate status history table.
/// </summary>
public class AuditEvent : Entity
{
    /// <summary>Empty for system or background job actions.</summary>
    public Guid? ActorAccountId { get; set; }
    public Account? ActorAccount { get; set; }

    public string Action { get; set; } = string.Empty;

    public string RecordType { get; set; } = string.Empty;
    public Guid? RecordId { get; set; }
    public string? RecordCode { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public string? Reason { get; set; }

    /// <summary>Before/after values (JSON); never contains passwords or verification codes (NFR-01).</summary>
    public string? Before { get; set; }
    public string? After { get; set; }

    /// <summary>Zone the event belongs to, used to search the log by zone (ACS-11).</summary>
    public Guid? ZoneId { get; set; }

    /// <summary>
    /// Never set: since 22/10 the audit search (ACS-11) is Cooperative Owner only, so there is no view
    /// scope to store. Kept for a later per-role audit view.
    /// </summary>
    public string? ViewScope { get; set; }
}
