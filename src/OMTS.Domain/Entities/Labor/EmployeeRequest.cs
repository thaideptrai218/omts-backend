using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// A request an employee sends to the Cooperative Owner: leave, or a missed check-in or check-out with the requested time
/// (LABOR-10, LABOR-05, BR-LABOR-08). Awaiting review until the Owner approves or rejects it or the employee cancels it; a
/// reviewed request does not change any more. It has no zone.
/// </summary>
public class EmployeeRequest : AuditableEntity, IHasStatus
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public EmployeeRequestType Type { get; set; }

    /// <summary>The first day of leave, or the day of a missed check-in or check-out.</summary>
    public DateOnly FromDate { get; set; }

    /// <summary>The last day of leave; the same as <see cref="FromDate"/> for a missed check-in or check-out.</summary>
    public DateOnly ToDate { get; set; }

    /// <summary>The time of the day (UTC+7) to record, for a missed check-in or check-out.</summary>
    public TimeOnly? RequestedTime { get; set; }

    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }

    public int StatusId { get; set; } = StatusIds.EmployeeRequest.AwaitingReview;
    public Status Status { get; set; } = null!;

    public string? ReviewNote { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedByAccountId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Chosen by the client for one submit, so a retry of the same one returns the request already saved.</summary>
    public Guid? ClientOperationId { get; set; }
}
