namespace OMTS.Application.Features.Sales;

public sealed record SubmittedPurchaseRequest(PurchaseRequestView Request, bool Replayed);
