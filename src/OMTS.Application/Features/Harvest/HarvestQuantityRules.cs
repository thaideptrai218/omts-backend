using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// What the cycle plan says about the harvest of a lot being saved (PM decision 11/11/2026, replacing "no cap" of BR-HARVEST-02): the target yield of the cycle, the estimated kg of its other
/// lots, and the Zone Manager's answer to the warning (<see cref="Confirm"/> and <see cref="Reason"/>).
/// </summary>
/// <param name="TargetYieldKg">The cycle's own estimate (BR-CYCLE-01); null: no cap.</param>
/// <param name="OtherLotsKg">The estimated kg of every other lot of the cycle (the lot being corrected leaves its own out).</param>
internal sealed record HarvestPlanInput(decimal? TargetYieldKg, decimal OtherLotsKg, bool Confirm, string? Reason);

/// <summary>The numbers of a harvest, as pure functions so every limit is tested in one place (BR-HARVEST-02, PM decision 11/11/2026).</summary>
internal static class HarvestQuantityRules
{
    /// <summary>The most cages a lot holds: far above any raft, so only a slip of the finger is refused.</summary>
    public const decimal MaxCages = 100_000m;

    /// <summary>Outside this share of cages times the kg per cage (to <see cref="MaxKgShare"/>) the estimated kg only warns.</summary>
    public const decimal MinKgShare = 0.2m;

    public const decimal MaxKgShare = 2m;

    /// <summary>Above this share of the target the harvest needs a confirmation and a reason; above <see cref="HardOverPlan"/> it is refused.</summary>
    public const decimal SoftOverPlan = 0.10m;

    public const decimal HardOverPlan = 0.50m;

    public const string ConfirmFlag = "confirmOverPlan";
    public const string ReasonField = "Over plan reason";

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

    /// <summary>
    /// The cycle's harvested kg with this lot against its target: null while it is at most 110 % or there is no target; 409 asking for <c>confirmOverPlan</c> and a reason between 110 % and
    /// 150 %; 400 above 150 %. A confirmed lot returns its reason in <paramref name="reason"/> to keep on the lot.
    /// </summary>
    public static AppError? CheckPlan(HarvestPlanInput plan, decimal estimatedKg, out string? reason)
    {
        reason = null;
        if (plan.TargetYieldKg is not > 0)
        {
            return null;
        }

        var target = plan.TargetYieldKg.Value;
        var total = plan.OtherLotsKg + estimatedKg;
        if (total > target * (1 + HardOverPlan))
        {
            return AppError.Invalid(HarvestLotFieldReader.EstimatedKgField, $"The cycle would have {total:0.##} kg, more than {(1 + HardOverPlan) * 100:0}% of its target of {target:0.##} kg.");
        }

        if (total <= target * (1 + SoftOverPlan))
        {
            return null;
        }

        if (!plan.Confirm)
        {
            return AppError.Conflict(
                UserMessages.HarvestOverPlanQuestion(total, target),
                new Dictionary<string, object?>
                {
                    ["confirmFlag"] = ConfirmFlag,
                    ["reasonField"] = "overPlanReason",
                    ["targetYieldKg"] = target,
                    ["harvestedKg"] = plan.OtherLotsKg,
                    ["newKg"] = estimatedKg,
                    ["totalKg"] = total,
                    ["overPlanPercent"] = decimal.Round((total - target) / target * 100, 1),
                });
        }

        var trimmed = plan.Reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > HarvestLotFieldReader.MaxReasonLength)
        {
            return AppError.Invalid(ReasonField);
        }

        reason = trimmed;
        return null;
    }
}
