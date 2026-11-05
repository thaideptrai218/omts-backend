using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Farming;

/// <summary>
/// A photo of a raft (FARM-04), at most <see cref="Raft.MaxPhotos"/> per raft: each holds one slot of 1 to
/// <see cref="Raft.MaxPhotos"/>, and the database refuses a slot out of range or taken. Removing one deletes the row and
/// the file; the audit log keeps its key (GBR-02).
/// </summary>
public class RaftPhoto : AuditableEntity
{
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    /// <summary>The slot, also the display order. A removed photo leaves its slot free for the next one.</summary>
    public int SortOrder { get; set; }

    /// <summary>Storage key of the file; never a public link (reads use signed URLs).</summary>
    public string FileKey { get; set; } = string.Empty;
}
