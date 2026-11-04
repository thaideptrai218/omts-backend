namespace OMTS.Domain.Enums;

/// <summary>Why a harvest lot was closed (BR-HARVEST-04). A close with no leftover kg in HARVEST-02 has no reason.</summary>
public enum HarvestLotCloseReason
{
    SoldWholeLot,
    Unsold,
    Spoiled,
    TimeLimitReached,
}
