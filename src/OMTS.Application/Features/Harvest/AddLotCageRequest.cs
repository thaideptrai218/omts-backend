namespace OMTS.Application.Features.Harvest;

/// <summary>One cage put on the scale.</summary>
/// <param name="ClientOperationId">
/// Made by the app for this cage, so sending it again after a lost answer does not weigh the cage twice: the cage already saved under it is kept as it is. Optional.
/// </param>
/// <param name="ProductId">The product of the cage, one of the products of the lot's lines. Left out, the product of the lot's only line (a lot holds one line), or none for a lot with no lines.</param>
/// <param name="Kg">Above 0, at most 3 decimals.</param>
public sealed record AddLotCageRequest(Guid? ClientOperationId, Guid? ProductId, decimal? Kg);
