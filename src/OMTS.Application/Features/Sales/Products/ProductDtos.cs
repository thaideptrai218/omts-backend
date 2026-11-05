namespace OMTS.Application.Features.Sales.Products;

public sealed record SaveProductRequest(string? Name, decimal? UnitPrice, string? RowVersion = null);

/// <param name="UnitPrice">The list price per kg in VND; null for a trader, who sends requests with no price (BR-SALE-01).</param>
public sealed record ProductView(Guid Id, string Name, decimal? UnitPrice, int StatusId, string Status, string RowVersion);

/// <summary>A saved product with MSG-GEN-05 (SALE-11 step 5).</summary>
public sealed record ProductSaved(ProductView Product, string Message, string MessageCode);
