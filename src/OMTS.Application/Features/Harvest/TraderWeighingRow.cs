namespace OMTS.Application.Features.Harvest;

/// <summary>A lot harvested for the trader's orders, on the trader's list of weighing sheets.</summary>
/// <param name="OrderCodes">The orders (not cancelled) the lot's harvest plan serves.</param>
/// <param name="ProductName">The product of the lot's line; null for a lot with no product lines.</param>
/// <param name="CageCount">The cages the cooperative weighed.</param>
/// <param name="TraderCageCount">The cages the trader weighed.</param>
/// <param name="WeighedKg">The cooperative's completed weighing; null until it is completed.</param>
/// <param name="Agreed">The trader agreed to the weighing the lot has now.</param>
/// <param name="NeedsAgreement">Not agreed yet: every lot of a trader needs the agreement before it is invoiced.</param>
public sealed record TraderWeighingRow(
    Guid LotId, string LotCode, DateTimeOffset HarvestedAt, IReadOnlyList<string> OrderCodes, string? ProductName, int CageCount, decimal CagesKg, int TraderCageCount, decimal TraderCagesKg,
    decimal? WeighedKg, DateTimeOffset? WeighedAt, int StatusId, string Status, bool Agreed, bool NeedsAgreement);
