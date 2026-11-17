namespace OMTS.Application.Features.Sales.Weighings.Models;

/// <summary>The cages of one lot once their shapes are right.</summary>
internal sealed record PortWeighingLotEntries(Guid LotId, IReadOnlyList<decimal> CageKgs);
