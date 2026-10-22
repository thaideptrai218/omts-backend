using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities;

/// <summary>
/// E49 – Raft material costs at raft creation or when floats/lines are replaced. Exactly one Raft
/// Creation record per raft; editing requires a reason and writes AuditEvent (BR-27).
/// </summary>
public class RaftMaterialRecord : AuditableEntity
{
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public Guid RecordedByAccountId { get; set; }
    public Account RecordedByAccount { get; set; } = null!;

    public RaftMaterialRecordType RecordType { get; set; }
    public DateOnly RecordDate { get; set; }
    public string? ReplacementReason { get; set; }
    public string? PurchasePlaceNotes { get; set; }
    public string? InvoicePhotoUrl { get; set; }

    /// <summary>VND, rounded to thousands.</summary>
    public decimal TotalAmount { get; set; }

    public ICollection<RaftMaterialLine> Lines { get; set; } = [];
}
