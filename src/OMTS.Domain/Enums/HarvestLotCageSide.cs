namespace OMTS.Domain.Enums;

/// <summary>Whose scale weighed a cage of a harvest lot at the port.</summary>
public enum HarvestLotCageSide
{
    /// <summary>The cooperative's scale: the official weighing, counted in the kg of the lot.</summary>
    Cooperative,

    /// <summary>The trader's own phone, beside the cooperative: compared with the cooperative's cages, never counted.</summary>
    Trader,
}
