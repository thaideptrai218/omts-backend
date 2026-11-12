using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// One nursery batch (ươm giống, decision of 12/11/2026): seed pieces ("mảnh") of ONE seed lot taken out of the stock for one zone to get used to the water before they are hung on lanes.
/// It starts Nursing and the stock leaves at once (a use issue, <see cref="IssueId"/>). Completing it (v2, 13/11/2026) records only the survivors (counted by eye, so an estimate): the batch is then a
/// "kho ươm" pool that any number of cycles draw from, piece by piece, through their seeding records (<c>SeedingRecord.NurseryBatchId</c>); seeded = the sum of those records, remaining = survivors - seeded.
/// A Completed batch cannot be cancelled; the survivors can be corrected (not below what is seeded). The Owner or Zone Manager can Close it: the remainder is then written off as a loss estimate.
/// </summary>
public class NurseryBatch : AuditableEntity, IHasStatus
{
    public const int MaxNoteLength = 1000;
    public const int MaxPhotos = 3;

    /// <summary>NU000001, one sequence for the cooperative.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The nursery is a zone, not a raft (decision of 12/11/2026).</summary>
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>The one seed lot of the batch: a Seed line of a Completed purchase receipt, traced to its receipt and supplier.</summary>
    public Guid SeedLotLineId { get; set; }
    public PurchaseReceiptLine SeedLotLine { get; set; } = null!;

    /// <summary>Pieces put into the nursery, greater than 0, in the base unit of the seed item.</summary>
    public decimal QuantityIn { get; set; }

    public DateOnly StartDate { get; set; }
    public string? Note { get; set; }

    public int StatusId { get; set; } = StatusIds.NurseryBatch.Nursing;
    public Status Status { get; set; } = null!;

    /// <summary>The day the nursery was completed; also the start date of the cycles it created.</summary>
    public DateOnly? CompletedOn { get; set; }

    /// <summary>Pieces that survived, counted by eye (an estimate); null while Nursing and on a Cancelled batch. 0 to <see cref="QuantityIn"/>.</summary>
    public decimal? Survivors { get; set; }

    public Guid? CompletedByAccountId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>The use issue (Xuất dùng) that took the pieces out of the stock; Cancelled together with the batch.</summary>
    public Guid? IssueId { get; set; }
    public InputIssue? Issue { get; set; }

    /// <summary>The day the batch was closed (Closed, v2): the pieces left in the pool were written off.</summary>
    public DateOnly? ClosedOn { get; set; }

    /// <summary>The pieces left in the pool when it was closed: a loss estimate. Null until closed.</summary>
    public decimal? CloseLossPieces { get; set; }

    public Guid? ClosedByAccountId { get; set; }
    public string? CloseReason { get; set; }

    /// <summary>Kept in the database for the cycles completed before v2 (13/11/2026) but neither read nor written any more: the pool is the seeding records of the batch.</summary>
    public ICollection<NurseryBatchAllocation> Allocations { get; set; } = [];
    public ICollection<NurseryBatchPhoto> Photos { get; set; } = [];
}

/// <summary>Legacy (before v2, 13/11/2026): the share of the survivors of a batch given to one raft, and the cycle created for it. Kept as rows, never written or read by the code any more.</summary>
public class NurseryBatchAllocation : AuditableEntity
{
    public Guid NurseryBatchId { get; set; }
    public NurseryBatch NurseryBatch { get; set; } = null!;

    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public Guid CycleId { get; set; }
    public CultivationCycle Cycle { get; set; } = null!;

    /// <summary>Pieces, greater than 0. Seeding from the nursery is limited to it (hard cap).</summary>
    public decimal Quantity { get; set; }
}

/// <summary>Which step of the nursery a photo belongs to.</summary>
public enum NurseryPhotoPhase
{
    Start,
    Completion,
}

/// <summary>A photo of the start or of the completion of a batch, 1 to <see cref="NurseryBatch.MaxPhotos"/> of each, one file per slot.</summary>
public class NurseryBatchPhoto : AuditableEntity
{
    public Guid NurseryBatchId { get; set; }
    public NurseryBatch NurseryBatch { get; set; } = null!;

    public NurseryPhotoPhase Phase { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Storage key; reads use signed links.</summary>
    public string FileKey { get; set; } = string.Empty;
}
