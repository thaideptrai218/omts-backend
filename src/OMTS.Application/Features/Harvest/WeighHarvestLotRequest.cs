namespace OMTS.Application.Features.Harvest;

/// <summary>
/// Completes the weighing of a lot from its weighed cages (owner decision of 24/11/2026): the kg of each product line are the sum of its cages, or the kg of the lot the sum of every cage when it
/// has no lines. The same completes it again after a cage was corrected.
/// </summary>
/// <param name="RowVersion">The version of the lot the weighing sheet was read at (NFR-09).</param>
/// <param name="WeighedAt">When the oysters were weighed; left out it is now. Not before the harvest and not in the future.</param>
public sealed record WeighHarvestLotRequest(string? RowVersion, DateTimeOffset? WeighedAt);
