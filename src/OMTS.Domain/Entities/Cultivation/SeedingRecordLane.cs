using OMTS.Domain.Entities.Farming;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>A whole lane hung with seed in a seeding session (BR-CYCLE-02). A lane may be in several sessions: re-hanging traces it to every seed lot.</summary>
public class SeedingRecordLane
{
    public Guid SeedingRecordId { get; set; }
    public SeedingRecord SeedingRecord { get; set; } = null!;

    public Guid LaneId { get; set; }
    public Lane Lane { get; set; } = null!;
}
