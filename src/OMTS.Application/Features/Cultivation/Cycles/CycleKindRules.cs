using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// One cycle grows one kind of oyster (PM decision of 17/11/2026): the product of the cycle is the product its seed sells as ("Loại hàu bán ra" of the seed item), and every seeding session and nursery batch of the cycle
/// uses seed of that product. A cycle that has no product yet takes it from its first seeding.
/// </summary>
public static class CycleKindRules
{
    public const string SeedLotField = "Seed lot";
    public const string Hint = "The seed grows another oyster than the cycle";

    /// <summary>True when the cycle has no product yet, or the seed sells as the product of the cycle. A seed with no product never matches a cycle that has one.</summary>
    public static bool Matches(Guid? cycleProductId, Guid? seedProductId) => cycleProductId is null || seedProductId == cycleProductId;

    /// <summary>MSG-GEN-01 on <c>Seed lot</c> when the seed of the lot grows another oyster than the cycle; null otherwise. The lot comes with its item.</summary>
    public static AppError? Refusal(Guid? cycleProductId, PurchaseReceiptLine lot) =>
        Matches(cycleProductId, lot.Item?.ProductId) ? null : AppError.Invalid(SeedLotField, Hint);

    /// <summary>The product a cycle takes from the seed of its first seeding: only a cycle with none takes one, and only when the seed has one.</summary>
    public static Guid? ProductFromFirstSeeding(Guid? cycleProductId, PurchaseReceiptLine lot) => cycleProductId ?? lot.Item?.ProductId;
}
