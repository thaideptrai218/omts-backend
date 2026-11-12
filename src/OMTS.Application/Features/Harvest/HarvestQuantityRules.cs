namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The numbers of a harvest, as pure functions so every limit is tested in one place (BR-HARVEST-02, PM decision 11/11/2026). The target yield of the cycle is a plan, not a cap (owner
/// decision 12/11/2026): no rule here compares a harvest with it, so a harvest above the target saves like any other.
/// </summary>
internal static class HarvestQuantityRules
{
    /// <summary>The most cages a lot holds: far above any raft, so only a slip of the finger is refused.</summary>
    public const decimal MaxCages = 100_000m;

    /// <summary>Outside this share of cages times the kg per cage (to <see cref="MaxKgShare"/>) the estimated kg only warns.</summary>
    public const decimal MinKgShare = 0.2m;

    public const decimal MaxKgShare = 2m;

    /// <summary>More than 0 and at most <see cref="MaxCages"/>, in steps of 0.01 (the column is decimal(9,2)).</summary>
    public static bool CagesAreValid(decimal? cages) => cages is > 0 and <= MaxCages && decimal.Round(cages.Value, 2) == cages.Value;

    /// <summary>Cages times the kg per cage, to the 2 decimals a quantity has.</summary>
    public static decimal KgOf(decimal cages, decimal kgPerCage) => decimal.Round(cages * kgPerCage, 2, MidpointRounding.AwayFromZero);

    /// <summary>The kg sent is within 0.2 to 2 times cages times the kg per cage.</summary>
    public static bool KgIsNearCages(decimal kg, decimal cages, decimal kgPerCage)
    {
        var worked = cages * kgPerCage;
        return kg >= worked * MinKgShare && kg <= worked * MaxKgShare;
    }

    /// <summary>The estimated kg is only an estimate (SRS glossary), so only a kg above <see cref="HardKgShare"/> times cages times the kg per cage is refused.</summary>
    public const decimal HardKgShare = 10m;

    public static bool KgIsWithinCeiling(decimal kg, decimal cages, decimal kgPerCage) => kg <= cages * kgPerCage * HardKgShare;

    /// <summary>The hint of a kg above the ceiling.</summary>
    public static string KgCeilingHint(decimal cages, decimal kgPerCage) =>
        $"The estimated kg may be at most {decimal.Round(cages * kgPerCage * HardKgShare, 2):0.##} kg (10 times {cages:0.##} cages at {kgPerCage:0.##} kg per cage); leave it out to work it out.";

    public const string KgFarFromCagesCode = "ESTIMATED_KG_FAR_FROM_CAGES";

    /// <summary>A non-blocking warning when the estimated kg is outside 0.2 to 2 times cages times the kg per cage.</summary>
    public static IReadOnlyList<Farming.FarmingWarning> KgWarnings(decimal cages, decimal kgPerCage, decimal estimatedKg) =>
        KgIsNearCages(estimatedKg, cages, kgPerCage)
            ? []
            : [new Farming.FarmingWarning(
                KgFarFromCagesCode,
                $"The estimated kg is far from {cages:0.##} cages at {kgPerCage:0.##} kg per cage ({KgOf(cages, kgPerCage):0.##} kg).",
                new Dictionary<string, object?> { ["workedKg"] = KgOf(cages, kgPerCage), ["minKg"] = KgOf(cages, kgPerCage) * MinKgShare, ["maxKg"] = KgOf(cages, kgPerCage) * MaxKgShare })];

    /// <summary>The share of the target the harvested kg reaches, to 1 decimal; null without a target above 0. Above 100 the cycle is over its target, which is only information.</summary>
    public static decimal? TargetPercent(decimal? targetYieldKg, decimal harvestedKg) =>
        targetYieldKg is > 0 ? decimal.Round(harvestedKg / targetYieldKg.Value * 100, 1) : null;
}
