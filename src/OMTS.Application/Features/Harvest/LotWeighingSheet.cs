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
/// <param name="CanEdit">
/// The caller may weigh now, and the lot is not Closed. On the cooperative's sheet: weigh, correct or remove its cages and complete the weighing (a Zone Manager of the lot's zone or the Cooperative
/// Owner). On the trader's sheet: weigh, correct or remove the trader's own cages.
/// </param>
/// <param name="OrderCodes">The orders (not cancelled) the lot's harvest plan serves; empty for a lot outside a plan.</param>
/// <param name="TraderName">
/// The trader of the lot (the one trader account of those orders), who weighs beside the cooperative and agrees to the weighing (owner decision of 24/11/2026); null when the lot has none (walk-in
/// orders, a lot outside a plan, or orders of several traders): no trader cages and no agreement then.
/// </param>
/// <param name="TraderCages">The trader's cages in the order weighed, compared by position with <paramref name="Cages"/>; the cooperative's cages are the official weighing.</param>
/// <param name="Agreement">The trader's agreement to the weighing the lot has now; null when none, or when the weighing was completed again with another figure since.</param>
/// <param name="NeedsAgreement">The lot has a trader and no agreement to its weighing now: it cannot be invoiced yet.</param>
/// <param name="CanAgree">On the trader's sheet only: the weighing is completed, no cage waits for completing again, the trader has not agreed to it yet and the lot is not Closed.</param>
public sealed record LotWeighingSheet(
    Guid LotId, string LotCode, int StatusId, string Status, string RowVersion, decimal HarvestedCages, decimal EstimatedKg, IReadOnlyList<LotCageView> Cages,
    IReadOnlyList<LotCageProductTotal> Products, decimal CagesKg, decimal? WeighedKg, DateTimeOffset? WeighedAt, bool NeedsCompleting, bool CanEdit,
    IReadOnlyList<string> OrderCodes, string? TraderName, IReadOnlyList<LotTraderCageView> TraderCages, decimal TraderCagesKg, LotWeighingAgreement? Agreement, bool NeedsAgreement,
    bool CanAgree);
