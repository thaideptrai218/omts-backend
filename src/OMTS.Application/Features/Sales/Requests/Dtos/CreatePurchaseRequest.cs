namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record CreatePurchaseRequest(Guid ProductId, int RequestedCages, DateOnly PickupDate, string? Note, Guid ClientOperationId);
