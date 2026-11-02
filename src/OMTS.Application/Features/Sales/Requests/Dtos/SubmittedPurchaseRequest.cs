namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record SubmittedPurchaseRequest(PurchaseRequestView Request, bool Replayed);
