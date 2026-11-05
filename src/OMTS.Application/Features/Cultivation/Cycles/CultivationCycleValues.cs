using OMTS.Application.Features.Farming;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>What the start date of a cycle is checked against (BR-CYCLE-01).</summary>
/// <param name="PreviousCloseDate">The start date must be after it.</param>
/// <param name="FirstRecordDate">On an edit, the start date must not be after it.</param>
internal sealed record CycleDateLimits(DateOnly Today, DateOnly? PreviousCloseDate, DateOnly? FirstRecordDate);

/// <summary>The checked fields of a cycle start or edit (MSG-GEN-01 names the others), and the values the audit log keeps.</summary>
internal sealed record CultivationCycleValues(DateOnly StartDate, decimal? ExpectedSeedQuantity, DateOnly? ExpectedHarvestDate, decimal? TargetYieldKg)
{
    private const int SeedQuantityScale = 3;
    private const int TargetYieldScale = 2;

    public static FieldCheck Read(
        DateOnly? startDate, decimal? expectedSeedQuantity, DateOnly? expectedHarvestDate, decimal? targetYieldKg, CycleDateLimits limits, out CultivationCycleValues values)
    {
        var check = new FieldCheck();
        check.Check(startDate is { } start && IsAllowedStart(start, limits), "Start date");
        var seed = check.PositiveOptional(expectedSeedQuantity, "Expected seed quantity", SeedQuantityScale);
        check.Check(startDate is null || expectedHarvestDate is null || expectedHarvestDate > startDate, "Expected harvest date");
        var yield = check.PositiveOptional(targetYieldKg, "Target yield", TargetYieldScale);
        values = new CultivationCycleValues(startDate ?? default, seed, expectedHarvestDate, yield);
        return check;
    }

    private static bool IsAllowedStart(DateOnly start, CycleDateLimits limits) =>
        start <= limits.Today
        && (limits.PreviousCloseDate is not { } previous || start > previous)
        && (limits.FirstRecordDate is not { } first || start <= first);

    public void ApplyTo(CultivationCycle cycle)
    {
        cycle.StartDate = StartDate;
        cycle.ExpectedSeedQuantity = ExpectedSeedQuantity;
        cycle.ExpectedHarvestDate = ExpectedHarvestDate;
        cycle.TargetYieldKg = TargetYieldKg;
    }

    public static object Snapshot(CultivationCycle c) => new { c.StartDate, c.ExpectedSeedQuantity, c.ExpectedHarvestDate, c.TargetYieldKg };
}
