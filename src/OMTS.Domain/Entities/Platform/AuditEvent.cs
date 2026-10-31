using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Platform;

/// <summary>
/// E48 – Who changed what, when and why (NFR-02, GBR-02). Never edited or deleted. Also the history of every
/// group A status transition, so there is no separate status history table. Only the Cooperative Owner searches it
/// (ACCOUNT-10, BR-ACCOUNT-13).
/// </summary>
public class AuditEvent : Entity
{
    /// <summary>The account that acted; empty for an employee-app action or a system action.</summary>
    public Guid? ActorAccountId { get; set; }
    public Account? ActorAccount { get; set; }

    /// <summary>
    /// The employee who acted in the employee app (GBR-02). No foreign key yet: the employee app arrives with LABOR.
    /// The system is the actor when neither this nor <see cref="ActorAccountId"/> is set.
    /// </summary>
    public Guid? ActorEmployeeId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string RecordType { get; set; } = string.Empty;
    public Guid? RecordId { get; set; }
    public string? RecordCode { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public string? Reason { get; set; }

    /// <summary>Before/after values (JSON); never contains passwords or verification codes (NFR-01).</summary>
    public string? Before { get; set; }
    public string? After { get; set; }

    /// <summary>Zone the event belongs to, used to search the log by zone.</summary>
    public Guid? ZoneId { get; set; }
}
