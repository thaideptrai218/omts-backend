namespace OMTS.Domain.Enums;

/// <summary>What a lane segment between markers A/B represents (E18, BR-03).</summary>
public enum LaneSegmentType
{
    Seeded,
    PlannedHarvest,
    Harvested,
    WrittenOff,
}
