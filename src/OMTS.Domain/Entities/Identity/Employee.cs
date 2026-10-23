using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Identity;

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

    /// <summary>Set when the employment ends (Employed → Left), cleared when the person returns (LAB-01).</summary>
    public DateOnly? EmploymentEndDate { get; set; }

    /// <summary>Where the wage is paid; all three fields or none.</summary>
    public BankAccountInfo? BankAccount { get; set; }

    /// <summary>Zones the person usually works in (pickers of LAB-03/LAB-04); grants no permission.</summary>
    public ICollection<EmployeeWorkingZone> WorkingZones { get; set; } = [];
}
