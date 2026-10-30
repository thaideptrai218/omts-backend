namespace OMTS.Application.Features.Sales;

public sealed record SaveSalesProduct(string? Code, string? Name, string? Description, bool InUse = true, string? RowVersion = null);
