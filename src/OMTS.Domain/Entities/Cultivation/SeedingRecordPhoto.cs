using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>A photo taken on site of a seeding session (decision of 12/11/2026): a new session holds 1 to <see cref="SeedingRecord.MaxPhotos"/>, one file per slot; a session saved before holds none.</summary>
public class SeedingRecordPhoto : AuditableEntity
{
    public Guid SeedingRecordId { get; set; }
    public SeedingRecord SeedingRecord { get; set; } = null!;

    public int SortOrder { get; set; }

    /// <summary>Storage key; reads use signed links.</summary>
    public string FileKey { get; set; } = string.Empty;
}

/// <summary>A photo of the plan of a cycle started without a nursery batch (decision of 12/11/2026): 1 to <see cref="CultivationCycle.MaxPlanPhotos"/>, one file per slot. A cycle from a nursery batch has none: it shows the completion photos of its batch.</summary>
public class CyclePlanPhoto : AuditableEntity
{
    public Guid CycleId { get; set; }
    public CultivationCycle Cycle { get; set; } = null!;

    public int SortOrder { get; set; }

    /// <summary>Storage key; reads use signed links.</summary>
    public string FileKey { get; set; } = string.Empty;
}
