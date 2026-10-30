namespace OMTS.Application.Features.Sales;

public sealed record CancelPurchaseRequest(string? Reason, string? RowVersion);
