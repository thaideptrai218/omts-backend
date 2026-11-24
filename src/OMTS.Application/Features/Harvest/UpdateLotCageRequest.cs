namespace OMTS.Application.Features.Harvest;

/// <summary>A cage weighed again, or put under another product of the lot.</summary>
/// <param name="ProductId">One of the products of the lot's lines. Left out, the product of the lot's only line, or none for a lot with no lines.</param>
/// <param name="Kg">Above 0, at most 3 decimals.</param>
public sealed record UpdateLotCageRequest(Guid? ProductId, decimal? Kg);
