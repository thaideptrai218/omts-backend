namespace OMTS.Application.Features.Harvest;

/// <summary>One product line of the lot on the weighing sheet: what the harvest counted, the cages weighed for it so far and the kg of the last completed weighing.</summary>
/// <param name="HarvestedCages">The cages of the line counted at the harvest (half a cage is 0.5).</param>
/// <param name="WeighedCages">How many cages of this product were put on the scale.</param>
/// <param name="CagesKg">The sum of those cages.</param>
/// <param name="WeighedKg">The kg of the line in the last completed weighing; null until the weighing is completed.</param>
public sealed record LotCageProductTotal(
    int LineNumber, Guid ProductId, string ProductCode, string ProductName, decimal HarvestedCages, int WeighedCages, decimal CagesKg, decimal? WeighedKg);
