using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E41 – Attendance of one person on one day (exactly one of EmployeeId/WorkerId), one record per person per
/// day across all zones (LAB-04). The team of the day is stored per row so a later team change does not
/// rewrite history (BR-20).
/// </summary>
public class AttendanceRecord : AuditableEntity
{
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public Guid? WorkerId { get; set; }
    public SeasonalWorker? Worker { get; set; }

    /// <summary>Team of the day; defaults to the current team of the worker.</summary>
    public Guid? ContractorId { get; set; }
    public Contractor? Contractor { get; set; }

    public DateOnly WorkDate { get; set; }
    public AttendanceResult Result { get; set; }

    /// <summary>0.5 to 24 in steps of 0.5; informational only, never changes pay.</summary>
    public decimal? Hours { get; set; }
    public string? Notes { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];
}
