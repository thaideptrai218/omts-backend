using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// One nursery batch (ươm giống, decision of 12/11/2026): seed pieces ("mảnh") of ONE seed lot taken out of the stock for one zone to get used to the water before they are hung on lanes.
/// It starts Nursing and the stock leaves at once (a use issue, <see cref="IssueId"/>). Completing it records the survivors (counted by eye, so an estimate) and gives each chosen raft of
/// the zone a cultivation cycle holding its share of the survivors (<see cref="NurseryBatchAllocation"/>): everything that survived is farmed, so the shares add up to the survivors exactly.
/// A Completed batch cannot be cancelled; the survivors and the shares can still be corrected before the seed is hung.
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

    public ICollection<NurseryBatchAllocation> Allocations { get; set; } = [];
    public ICollection<NurseryBatchPhoto> Photos { get; set; } = [];
}

/// <summary>The share of the survivors of a batch given to one raft, and the cycle created for it. The shares of a batch add up to its survivors.</summary>
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
