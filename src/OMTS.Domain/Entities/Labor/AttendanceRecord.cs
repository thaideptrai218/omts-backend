using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities;

/// <summary>
/// E41 – Attendance of one person on one day (exactly one of EmployeeId/WorkerId). The contractor of
/// the day is stored per row so the right intermediary is paid (BR-20).
/// </summary>
public class AttendanceRecord : AuditableEntity
{
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public Guid? WorkerId { get; set; }
    public SeasonalWorker? Worker { get; set; }

    public Guid? ContractorId { get; set; }
    public Contractor? Contractor { get; set; }

    public DateOnly WorkDate { get; set; }
    public AttendanceResult Result { get; set; }
    public string? Notes { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];
}
