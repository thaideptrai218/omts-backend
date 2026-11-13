using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The check-in and check-out of one employee on one day, each with its recording method (LABOR-04, BR-LABOR-06). One record per
/// employee and day. A record without a check-in or without a check-out is incomplete and counts no hours or day worked.
/// </summary>
public class Attendance : AuditableEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>Business date (UTC+7) of the work.</summary>
    public DateOnly Date { get; set; }

    public DateTimeOffset? CheckInAt { get; set; }
    public DateTimeOffset? CheckOutAt { get; set; }

    public AttendanceMethod? CheckInMethod { get; set; }
    public AttendanceMethod? CheckOutMethod { get; set; }

    public Guid? CheckInRecordedByAccountId { get; set; }
    public Guid? CheckOutRecordedByAccountId { get; set; }

    /// <summary>The reason of a crew start or a correction.</summary>
    public string? CheckInReason { get; set; }
    public string? CheckOutReason { get; set; }

    public bool IsComplete => CheckInAt is not null && CheckOutAt is not null;
}
