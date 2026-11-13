using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// One manual change of the progress of a lane in a cycle (<c>PUT /api/rafts/{raftId}/lanes/{n}/progress</c>): the totals as they were and as they became, with who, when and why. It is both the history and
/// the correction: the totals of a lane are the last change's totals plus the sessions and lots recorded after it. Never changed or removed.
/// </summary>
public class LaneProgressChange : AuditableEntity
{
    public const int MaxReasonLength = 500;

    public Guid CycleId { get; set; }
    public Guid LaneId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="OccurredAt"/>.</summary>
    public DateOnly OccurredOn { get; set; }

    public Guid ChangedByAccountId { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>The totals before the change; <see cref="OldHarvestedPercent"/> null when it was not entered.</summary>
    public decimal OldSeededPercent { get; set; }
    public decimal? OldHarvestedPercent { get; set; }

    /// <summary>The totals after the change: what the lane holds from now on (the other total as it was when only one was sent).</summary>
    public decimal SeededPercent { get; set; }
    public decimal? HarvestedPercent { get; set; }
}
