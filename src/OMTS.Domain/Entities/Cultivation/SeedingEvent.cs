using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>E17 – One seeding session of a cycle. Read only for the quantity seeded per seed lot, until CYCLE replaces it with SeedingRecord.</summary>
public class SeedingEvent : AuditableEntity
{
    public Guid CycleId { get; set; }
    public CultivationCycle Cycle { get; set; } = null!;

    public DateOnly SeedingDate { get; set; }

    public Guid SeedLotId { get; set; }
    public SeedReceiptLot SeedLot { get; set; } = null!;

    public Guid RecordedByAccountId { get; set; }
    public string? Workers { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];

    /// <summary>Optional, in the seed lot's original unit.</summary>
    public decimal? SeedQuantity { get; set; }

    /// <summary>Required when the seed differs from the cycle's planned seed type.</summary>
    public string? PlanDeviationReason { get; set; }
}
