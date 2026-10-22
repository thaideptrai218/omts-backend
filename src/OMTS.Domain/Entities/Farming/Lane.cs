using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>
/// E12 – Lane between two main lines, with a stable number. Its condition (not seeded, growing,
/// harvested, written off) is derived from <see cref="LaneSegment"/>s; there is no stored status.
/// </summary>
public class Lane : AuditableEntity
{
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public int LaneNumber { get; set; }

    /// <summary>Length in metres measured from marker A to marker B.</summary>
    public decimal? Length { get; set; }

    public string? MeasurementMarkers { get; set; }

    public ICollection<LaneSegment> Segments { get; set; } = [];
}
