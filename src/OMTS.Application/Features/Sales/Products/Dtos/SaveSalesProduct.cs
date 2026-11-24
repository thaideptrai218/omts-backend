namespace OMTS.Application.Features.Sales.Products.Dtos;

/// <param name="UnitPrice">The list price per kg in whole VND, above 0; a product that is created needs it, and one that is edited keeps its price when it is left out.</param>
public sealed record SaveSalesProduct(string? Code, string? Name, string? Description, bool InUse = true, string? RowVersion = null, decimal? UnitPrice = null);
