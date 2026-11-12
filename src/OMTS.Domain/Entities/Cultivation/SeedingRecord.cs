using OMTS.Domain.Common;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// One seeding session of a cycle: seed from one seed lot hung on one or more whole lanes on one day (BR-CYCLE-02). It has no business code and no status.
/// A session removed in CYCLE-02.2 is kept only in the audit log. Its total quantity counts in the quantity seeded from the seed lot (GBR-04).
/// </summary>
public class SeedingRecord : AuditableEntity
{
    public Guid CycleId { get; set; }
    public CultivationCycle Cycle { get; set; } = null!;

    /// <summary>Not before the start date of the cycle or the date the seed lot was received, and not later than today.</summary>
    public DateOnly SeedingDate { get; set; }

    /// <summary>The seed lot: a Seed line of a Completed purchase receipt (GBR-04).</summary>
    public Guid SeedLotLineId { get; set; }
    public PurchaseReceiptLine SeedLotLine { get; set; } = null!;

    /// <summary>The total seed hung, greater than 0, in the one unit of every seed item. Not compared with what is left in the lot (GBR-04).</summary>
    public decimal Quantity { get; set; }

    /// <summary>Who recorded it: a Zone Manager corrects only the sessions they recorded (BR-CYCLE-02).</summary>
    public Guid RecordedByAccountId { get; set; }

    /// <summary>The stock issue (Xuất dùng) this session posted, one fact with it: null on a session recorded before stock was checked (legacy, its seed is counted from the session itself).</summary>
    public Guid? IssueId { get; set; }
    public InputIssue? Issue { get; set; }

    /// <summary>
    /// The nursery batch the seed came from: the session is capped by the cycle's allocation and posts no issue (the stock left when the nursery started), and <see cref="SeedLotLineId"/> is the
    /// batch's lot, kept for tracing. Null on a session of a cycle started the old way.
    /// </summary>
    public Guid? NurseryBatchId { get; set; }

    /// <summary>The reason given when the seed of the cycle went above 110 % of its planned seed quantity (confirmed by the caller); null otherwise.</summary>
    public string? OverPlanReason { get; set; }

    public ICollection<SeedingRecordLane> Lanes { get; set; } = [];
}
