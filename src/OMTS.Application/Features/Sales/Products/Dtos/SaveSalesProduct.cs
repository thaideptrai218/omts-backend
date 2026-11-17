namespace OMTS.Application.Features.Sales.Products.Dtos;

public sealed record SaveSalesProduct(string? Code, string? Name, string? Description, bool InUse = true, string? RowVersion = null);
