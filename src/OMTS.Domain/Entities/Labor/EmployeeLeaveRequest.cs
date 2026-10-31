using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
namespace OMTS.Domain.Entities.Labor;

/// <summary>LABOR-10/05: leave decisions belong to the Cooperative Owner.</summary>
public class EmployeeLeaveRequest : AuditableEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool? Approved { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? ReviewedByAccountId { get; set; }
    public string? Response { get; set; }
    public Guid ClientOperationId { get; set; }
}
