namespace OMTS.Application.Features.Sales;

public sealed record PurchaseRequestPage(IReadOnlyList<PurchaseRequestView> Items, int Page, int PageSize, int Total);
