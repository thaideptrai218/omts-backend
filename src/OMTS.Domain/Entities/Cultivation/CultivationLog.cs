using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// One visit to a raft during its Open cycle (BR-CYCLE-05), with at least one photo and optional notes. It has no business code and no
/// status. Its photos are for cooperative staff only and never public (BR-FARM-09). A log removed in CYCLE-04.1 is kept only in the audit log.
/// </summary>
public class CultivationLog : AuditableEntity
{
    /// <summary>The most photos a log holds (default of the PM question on the cap).</summary>
    public const int MaxPhotos = 10;

    public Guid CycleId { get; set; }
    public CultivationCycle Cycle { get; set; } = null!;

    /// <summary>From the start date of the cycle to today; the log may be recorded after the visit.</summary>
    public DateOnly VisitDate { get; set; }

    public string? Notes { get; set; }

    /// <summary>Who recorded it: a Zone Manager corrects only the logs they recorded (BR-CYCLE-05).</summary>
    public Guid RecordedByAccountId { get; set; }

    public ICollection<CultivationLogPhoto> Photos { get; set; } = [];
}
