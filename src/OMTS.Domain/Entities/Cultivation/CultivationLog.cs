using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// One visit to a raft, with optional photos and notes. A raft with no cultivation cycle, or one that is Discontinued, still gets logs, so the repair work on it is recorded:
/// the cycle is worked out from the visit date when the log is saved (BR-CYCLE-05). It has no business code and no status. Its photos are for cooperative staff only and
/// never public (BR-FARM-09). A log removed in CYCLE-04.1 is kept only in the audit log.
/// </summary>
public class CultivationLog : AuditableEntity
{
    /// <summary>The most photos a log holds (default of the PM question on the cap).</summary>
    public const int MaxPhotos = 10;

    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    /// <summary>The Open cycle of the raft that the visit date falls in; null when the date is in no cycle. A date inside a Closed cycle is never saved.</summary>
    public Guid? CycleId { get; set; }
    public CultivationCycle? Cycle { get; set; }

    /// <summary>Not after today; inside the cycle when it has one (from its start date); the log may be recorded after the visit.</summary>
    public DateOnly VisitDate { get; set; }

    /// <summary>The kind of visit, one of <see cref="CultivationLogTypes"/>; a log that names none is <c>OTHER</c>. A log still carries no code of any cycle.</summary>
    public string LogType { get; set; } = CultivationLogTypes.Other;

    public string? Notes { get; set; }

    /// <summary>Who recorded it: a Zone Manager corrects only the logs they recorded (BR-CYCLE-05).</summary>
    public Guid RecordedByAccountId { get; set; }

    public ICollection<CultivationLogPhoto> Photos { get; set; } = [];
}
