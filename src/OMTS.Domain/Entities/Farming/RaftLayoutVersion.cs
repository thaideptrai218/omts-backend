using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>
/// E11 – Versioned raft layout so lane markers are re-read correctly when the raft changes (BR-02/03).
/// Markers are not changed while the raft has an unclosed cycle (BR-25).
/// </summary>
public class RaftLayoutVersion : AuditableEntity
{
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public int VersionNumber { get; set; }

    /// <summary>Number of lanes N; the raft then has N + 1 longitudinal lines numbered 0…N (BR-28).</summary>
    public int LaneCount { get; set; }

    /// <summary>Layout map and length markers (JSON).</summary>
    public string? LayoutMap { get; set; }

    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
}
