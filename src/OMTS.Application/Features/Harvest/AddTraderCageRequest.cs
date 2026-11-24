namespace OMTS.Application.Features.Harvest;

/// <summary>One cage the trader put on the scale, beside the cooperative.</summary>
/// <param name="ClientOperationId">Made by the app for this cage, so sending it again after a lost answer does not weigh the cage twice. Optional.</param>
/// <param name="Kg">Above 0, at most 3 decimals.</param>
public sealed record AddTraderCageRequest(Guid? ClientOperationId, decimal? Kg);
