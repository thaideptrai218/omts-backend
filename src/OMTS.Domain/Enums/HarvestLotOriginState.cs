namespace OMTS.Domain.Enums;

/// <summary>The origin information of a harvest lot, worked out from its publish and withdrawal times (BR-TRACE-02).</summary>
public enum HarvestLotOriginState
{
    NotPublished,
    Published,
    Withdrawn,
}
