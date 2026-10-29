namespace OMTS.Application.Common.Models;

/// <summary>Location exchanged on the server; tokens and location are not persisted by this lookup.</summary>
public sealed record ZaloLocationLookup(ZaloLookupOutcome Outcome, decimal? Latitude = null, decimal? Longitude = null)
{
    public static readonly ZaloLocationLookup Rejected = new(ZaloLookupOutcome.Rejected);
    public static readonly ZaloLocationLookup Unavailable = new(ZaloLookupOutcome.Unavailable);
}
