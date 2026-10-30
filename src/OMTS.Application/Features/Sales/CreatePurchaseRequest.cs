namespace OMTS.Application.Features.Sales;

public sealed record CreatePurchaseRequest(Guid ProductId, int RequestedCages, DateOnly PickupDate, string? Note, Guid ClientOperationId);
