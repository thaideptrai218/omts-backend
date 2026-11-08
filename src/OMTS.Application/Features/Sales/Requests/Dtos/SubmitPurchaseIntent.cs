namespace OMTS.Application.Features.Sales.Requests.Dtos;

/// <summary>Explicit units at the shared endpoint: the Mini App supplies cages, existing web clients supply kg.</summary>
public sealed record SubmitPurchaseIntent(Guid? ProductId, int? RequestedCages, decimal? RequestedKg,
    DateOnly? PickupDate, string? Note, Guid? ClientOperationId);
