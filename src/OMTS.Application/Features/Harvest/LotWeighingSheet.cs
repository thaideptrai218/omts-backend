namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The weighing sheet (phiếu cân) of a lot: every cage weighed at the port, the sum per product and the last completed weighing (owner decision of 24/11/2026).
/// </summary>
/// <param name="RowVersion">The version of the lot; send it back to complete the weighing. Weighing or correcting a cage does not change it.</param>
/// <param name="HarvestedCages">The cages counted at the harvest, to compare with the cages weighed; a difference is allowed (half a cage is weighed as one).</param>
/// <param name="Products">The product lines of the lot with their cages; empty for a lot with no product lines, whose cages have no product.</param>
/// <param name="CagesKg">The sum of every cage weighed.</param>
/// <param name="WeighedKg">The kg of the lot in the last completed weighing; null until the weighing is completed.</param>
/// <param name="NeedsCompleting">
/// The cages differ from the last completed weighing: never completed, or a cage was weighed, corrected or removed since. The lot keeps the kg of the last completed weighing until then.
/// </param>
/// <param name="CanEdit">The caller may weigh, correct or remove cages and complete the weighing now: a Zone Manager of the lot's zone or the Cooperative Owner, and the lot is not Closed.</param>
public sealed record LotWeighingSheet(
    Guid LotId, string LotCode, int StatusId, string Status, string RowVersion, decimal HarvestedCages, decimal EstimatedKg, IReadOnlyList<LotCageView> Cages,
    IReadOnlyList<LotCageProductTotal> Products, decimal CagesKg, decimal? WeighedKg, DateTimeOffset? WeighedAt, bool NeedsCompleting, bool CanEdit);
