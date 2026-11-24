namespace OMTS.Application.Features.Harvest;

/// <summary>One weighed cage of a lot, in the order weighed.</summary>
/// <param name="Number">The number given when the cage was weighed; a removed cage leaves a gap, so show the position in the list.</param>
/// <param name="ProductId">The product of the cage; null for a lot with no product lines.</param>
/// <param name="WeighedAt">When the cage was weighed, or last corrected.</param>
public sealed record LotCageView(Guid Id, int Number, Guid? ProductId, decimal Kg, DateTimeOffset WeighedAt);
