using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>E03 – Personnel profile for work assignment, attendance and pay; separate from the login account (BR-LABOR-01).</summary>
public class Employee : AuditableEntity, IHasStatus
{
    public Guid CooperativeId { get; set; }
    public Cooperative Cooperative { get; set; } = null!;

    /// <summary>
    /// Employee code, NV000001 from the NV document sequence when the user leaves it empty, or typed (upper-case letters, digits and
    /// "-", at most 20 characters). Unique within the cooperative; null only for a row older than the column (the migration fills those).
    /// </summary>
    public string? Code { get; set; }

    public string FullName { get; set; } = string.Empty;

    /// <summary>Optional, needed only for the employee app; when entered, no other Employed employee has it.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// The staff account created with this profile, a technical link (the SRS matches by phone number): at most one
    /// account, and it grants no permission by itself.
    /// </summary>
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }

    public DateOnly? Birthday { get; set; }

    public Gender? Gender { get; set; }

    /// <summary>Citizen ID (CCCD) of 12 digits, or an older 9-digit ID card. Personal data: shown only to those who maintain the employee, never exported.</summary>
    public string? IdNumber { get; set; }

    /// <summary>The first day of work; not the same as the date the employee type applies from.</summary>
    public DateOnly? StartDate { get; set; }
    public string? Address { get; set; }
    public string? Area { get; set; }
    public string? Ward { get; set; }
    public string? OtherInformation { get; set; }
    public string? Notes { get; set; }

    public int StatusId { get; set; } = StatusIds.Employee.Working;
    public Status Status { get; set; } = null!;

    /// <summary>
    /// Entered when the employment ends, not later than today; the employee is Left from the next day, and it is cleared
    /// when the employment is resumed (BR-LABOR-01).
    /// </summary>
    public DateOnly? LastWorkingDay { get; set; }

    /// <summary>Where the wage is paid; all three fields or none.</summary>
    public BankAccountInfo? BankAccount { get; set; }

    /// <summary>Who maintains the employee (BR-LABOR-01) and whose pickers list them first; grants no permission.</summary>
    public ICollection<EmployeeWorkingZone> WorkingZones { get; set; } = [];

    /// <summary>The employee type and every earlier one, each from its date.</summary>
    public ICollection<EmployeeTypePeriod> TypePeriods { get; set; } = [];

    public ICollection<EmployeeJobType> JobTypes { get; set; } = [];
}
