namespace OMTS.Application.Features.Sales.Weighings.Dtos;

/// <summary>The cages of one harvest lot as weighed.</summary>
/// <param name="CageKgs">The kg of each cage in the order weighed.</param>
public sealed record PortWeighingLotEntry(Guid? HarvestLotId, IReadOnlyList<decimal?>? CageKgs);
