using OMTS.Domain.Entities.Farming;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>A whole lane hung with seed in a seeding session (BR-CYCLE-02). A lane may be in several sessions: re-hanging traces it to every seed lot.</summary>
public class SeedingRecordLane
{
    public Guid SeedingRecordId { get; set; }
    public SeedingRecord SeedingRecord { get; set; } = null!;

    public Guid LaneId { get; set; }
    public Lane Lane { get; set; } = null!;

    /// <summary>
    /// The share of the lane this session hung, above 0 and at most 100, two decimals (owner decision of 12/11/2026). Entered in the form, default 100 (a whole lane); the shares of a lane add up to at most 100
    /// in a cycle. Rows saved before the percentages are 100.
    /// </summary>
    public decimal SeededPercent { get; set; } = 100m;
}
