namespace OMTS.Application.Features.Sales.Products.Dtos;

public sealed record SalesProductView(Guid Id, string Code, string Name, string? Description, bool InUse, bool AvailableForPurchase, string RowVersion);
