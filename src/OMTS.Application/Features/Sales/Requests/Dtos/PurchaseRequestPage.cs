namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record PurchaseRequestPage(IReadOnlyList<PurchaseRequestView> Items, int Page, int PageSize, int Total);
