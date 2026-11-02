namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record CancelPurchaseRequest(string? Reason, string? RowVersion);
