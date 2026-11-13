namespace OMTS.Application.Features.Sales.Weighings.Dtos;

public sealed record CreatePortWeighingRequest(Guid? PurchaseRequestId, IReadOnlyList<PortWeighingLotEntry?>? Lots);
