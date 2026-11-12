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

    /// <summary>
    /// The product farmed on the raft in this cycle, fixed when the cycle starts and changeable while the cycle has no harvest lot. Required for a
    /// new cycle (checked on the write, not by the column): a cycle closed before products existed has none.
    /// </summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Plan estimate, greater than 0.</summary>
    public decimal? ExpectedSeedQuantity { get; set; }

    /// <summary>Plan estimate, after <see cref="StartDate"/>.</summary>
    public DateOnly? ExpectedHarvestDate { get; set; }

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

    public int StatusId { get; set; } = StatusIds.CultivationCycle.Open;
    public Status Status { get; set; } = null!;
}
