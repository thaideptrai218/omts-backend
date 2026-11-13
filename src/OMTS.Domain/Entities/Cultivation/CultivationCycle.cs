using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// One farming round on a raft, from the start of seeding to the close (BR-CYCLE-01). It has no business code. A raft holds at
/// most one Open cycle and its cycles never overlap; a cycle is open on a date from <see cref="StartDate"/> to
/// <see cref="CloseDate"/>, both included. A cycle started by mistake is removed (kept only in the audit log); a Closed one is
/// never reopened (BR-CYCLE-04).
/// </summary>
public class CultivationCycle : AuditableEntity, IHasStatus
{
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    /// <summary>Not later than today and after the close date of the raft's previous cycle.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Plan estimate, greater than 0.</summary>
    public decimal? ExpectedSeedQuantity { get; set; }

    /// <summary>Kept in the table but no longer read or written (owner decision of 12/11/2026: the plan has no expected harvest date).</summary>
    public DateOnly? ExpectedHarvestDate { get; set; }

    /// <summary>The nursery batch the cycle takes its seed from (chosen when the cycle is started, v2 13/11/2026; changeable until the first seeding record); null on a cycle started the old way, which keeps its old rules.</summary>
    public Guid? NurseryBatchId { get; set; }

    /// <summary>The product (kind of oyster) the cycle will yield and sell (owner decision of 13/11/2026): required to start a cycle, changeable until the first seeding record; a harvest lot can only be recorded for a purchase request of this product.
    /// Null on a cycle started before: it keeps no check.</summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Legacy (cycles completed from a nursery before v2): the share given to this cycle. Kept, no longer read or written.</summary>
    public decimal? NurseryAllocatedQuantity { get; set; }

    /// <summary>Free note of the plan (v2), at most 1000 characters.</summary>
    public string? Note { get; set; }

    public const int MaxNoteLength = 1000;

    /// <summary>Plan estimate in kg, greater than 0.</summary>
    public decimal? TargetYieldKg { get; set; }

    /// <summary>The day the seeding was marked complete, once per cycle (BR-CYCLE-02).</summary>
    public DateOnly? SeedingCompletedOn { get; set; }

    /// <summary>The day the cycle was closed; it still belongs to the cycle.</summary>
    public DateOnly? CloseDate { get; set; }

    public string? Assessment { get; set; }

    /// <summary>The close was an early close (BR-CYCLE-07). The summary itself is never stored (BR-CYCLE-06).</summary>
    public bool ClosedEarly { get; set; }

    /// <summary>When MSG-CYCLE-08 was sent, so it is sent once.</summary>
    public DateTimeOffset? CloseRemindedAt { get; set; }

    public const int MaxPlanPhotos = 3;

    /// <summary>Photos of the plan, 1 to <see cref="MaxPlanPhotos"/> on a cycle started without a nursery batch since 12/11/2026; none on an older one, none on one from a nursery batch (it shows the batch's completion photos).</summary>
    public ICollection<CyclePlanPhoto> PlanPhotos { get; set; } = [];

    public int StatusId { get; set; } = StatusIds.CultivationCycle.Open;
    public Status Status { get; set; } = null!;
}
