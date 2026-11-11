namespace OMTS.Application.Features.Sales.Weighings.Dtos;

/// <param name="Kg">The sum of <paramref name="CageKgs"/>.</param>
public sealed record PortWeighingLotView(Guid HarvestLotId, string LotCode, decimal Kg, IReadOnlyList<decimal> CageKgs);
