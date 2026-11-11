namespace OMTS.Application.Features.Sales.Weighings.Dtos;

public sealed record UpdatePortWeighingRequest(IReadOnlyList<PortWeighingLotEntry?>? Lots, string? RowVersion);
