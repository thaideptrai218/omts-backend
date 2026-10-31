namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record RespondPurchaseRequest(bool Accept, int? AcceptedCages, DateOnly? PickupDate, bool AllowPayLater, string? Note, string? RowVersion);
