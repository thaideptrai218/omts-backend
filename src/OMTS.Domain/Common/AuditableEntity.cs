namespace OMTS.Domain.Common;

/// <summary>
/// Entity stamped with who created/changed it and when (set on save). RowVersion backs the
/// optimistic concurrency check that blocks overwriting a newer version (NFR-09, MSG06).
/// </summary>
public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
