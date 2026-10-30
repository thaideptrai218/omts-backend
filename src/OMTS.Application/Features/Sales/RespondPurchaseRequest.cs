namespace OMTS.Application.Features.Sales;

public sealed record RespondPurchaseRequest(bool Accept, int? AcceptedCages, DateOnly? PickupDate, bool AllowPayLater, string? Note, string? RowVersion);
