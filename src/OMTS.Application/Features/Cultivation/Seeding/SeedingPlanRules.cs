using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>What the plan check reads: the planned seed of the cycle, the seed of its other sessions and the caller's answer to the question.</summary>
internal sealed record SeedingPlanInput(decimal? PlannedSeedQuantity, decimal OtherSessionsQuantity, bool Confirm, string? Reason);

/// <summary>
/// The seed of a cycle against its plan (decision of 12/11/2026), the same shape as the harvest cap: the sum of the sessions of the cycle with this one against the planned seed quantity
/// (BR-CYCLE-01 estimate). Up to 110 % it is saved; above 110 % and up to 150 % the caller is asked (409 MSG-GEN-04) and sends <c>confirmOverPlan</c> with a reason, which the session and the
/// audit log keep; above 150 % it is refused (400). A cycle with no plan has no cap.
/// </summary>
public static class SeedingPlanRules
{
    public const decimal SoftOverPlan = 0.10m;
    public const decimal HardOverPlan = 0.50m;
    public const string ConfirmFlag = "confirmOverPlan";
    public const string OverPlanReasonField = "Over plan reason";

    /// <summary>Seeded against planned, as a percentage with one decimal; null without a plan.</summary>
    public static decimal? Percent(decimal? planned, decimal seeded) => planned is > 0 ? decimal.Round(seeded / planned.Value * 100, 1) : null;

    /// <summary>Null while within the plan (or without one); the reason to keep comes back in <paramref name="reason"/> for a confirmed session.</summary>
    internal static AppError? Check(SeedingPlanInput plan, decimal quantity, out string? reason)
    {
        reason = null;
        if (plan.PlannedSeedQuantity is not > 0)
        {
            return null;
        }

        var planned = plan.PlannedSeedQuantity.Value;
        var total = plan.OtherSessionsQuantity + quantity;
        if (total > planned * (1 + HardOverPlan))
        {
            return AppError.Invalid(SeedingValues.QuantityField, $"The cycle would have {total:0.###} seed, more than {(1 + HardOverPlan) * 100:0}% of its planned {planned:0.###}.");
        }

        if (total <= planned * (1 + SoftOverPlan))
        {
            return null;
        }

        if (!plan.Confirm)
        {
            return AppError.Conflict(
                UserMessages.SeedingOverPlanQuestion(total, planned),
                new Dictionary<string, object?>
                {
                    ["confirmFlag"] = ConfirmFlag,
                    ["reasonField"] = "overPlanReason",
                    ["plannedSeedQuantity"] = planned,
                    ["seededQuantity"] = plan.OtherSessionsQuantity,
                    ["newQuantity"] = quantity,
                    ["totalQuantity"] = total,
                    ["overPlanPercent"] = decimal.Round((total - planned) / planned * 100, 1),
                });
        }

        var trimmed = plan.Reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > SeedingValues.MaxReasonLength)
        {
            return AppError.Invalid(OverPlanReasonField);
        }

        reason = trimmed;
        return null;
    }
}
