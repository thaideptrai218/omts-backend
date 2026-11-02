namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record PurchaseRequestView(Guid Id, string Code, Guid ProductId, string ProductName, string QuantityUnit, int RequestedCages, DateOnly RequestedPickupDate, string? Note, string Status, int? AcceptedCages, DateOnly? AcceptedPickupDate, bool AllowPayLater, string? ResponseNote, DateTimeOffset? RespondedAt, DateTimeOffset? CancelledAt, string? CancellationReason, DateTimeOffset CreatedAt, string CustomerName, string RowVersion);
