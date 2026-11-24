namespace OMTS.Application.Features.Harvest;

/// <summary>A cage the trader weighed again.</summary>
/// <param name="Kg">Above 0, at most 3 decimals.</param>
public sealed record UpdateTraderCageRequest(decimal? Kg);
