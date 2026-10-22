using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>E03 – Personnel profile for work assignment, attendance and pay; separate from the login account.</summary>
public class Employee : AuditableEntity, IHasStatus
{
    public Guid CooperativeId { get; set; }
    public Cooperative Cooperative { get; set; } = null!;

    public string FullName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }

    /// <summary>At most one account; linking an account does not grant permissions by itself.</summary>
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }

    public DateOnly? Birthday { get; set; }
    public string? Address { get; set; }
    public string? Area { get; set; }
    public string? Ward { get; set; }
    public string? OtherInformation { get; set; }
    public string? Notes { get; set; }

    public int StatusId { get; set; } = StatusIds.Employee.Working;
    public Status Status { get; set; } = null!;
}
