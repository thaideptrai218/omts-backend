namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record RespondPurchaseShift(bool Approved, string? Reason, string? RowVersion);
