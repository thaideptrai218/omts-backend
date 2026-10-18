using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>
/// E33 – Goods that actually came back; separate from credit/refund. “Holding Overdue” is derived
/// after 3 days while not Resolved (BR-22, JOB-07).
/// </summary>
public class PhysicalReturn : AuditableEntity, IHasStatus
{
    public static readonly TimeSpan HoldingPeriod = TimeSpan.FromDays(3);

    public Guid SaleId { get; set; }
    public SaleTransaction Sale { get; set; } = null!;

    public Guid LotId { get; set; }
    public TraceabilityLot Lot { get; set; } = null!;

    public decimal ReceivedQuantityKg { get; set; }
    public string? Condition { get; set; }
    public string? HoldingPlace { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset HoldingDeadline { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];

    public decimal? ResellableQuantityKg { get; set; }
    public decimal? OtherDisposalQuantityKg { get; set; }
    public string? AssessmentBasis { get; set; }
    public Guid? AssessedByAccountId { get; set; }

    public int StatusId { get; set; } = StatusIds.PhysicalReturn.Holding;
    public Status Status { get; set; } = null!;

    /// <summary>Resale orders allocated from the resellable portion.</summary>
    public ICollection<SalesOrder> ResaleOrders { get; set; } = [];

    public bool IsHoldingOverdue(DateTimeOffset now) =>
        StatusId != StatusIds.PhysicalReturn.Disposed && now - ReceivedAt > HoldingPeriod;
}
