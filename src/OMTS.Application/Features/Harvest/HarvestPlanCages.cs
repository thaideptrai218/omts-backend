using System.Globalization;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The cages of a harvest lot against the cages the line of its harvest plan expects: only a warning, never a refusal, as the Zone Manager counts what is really harvested. A line with no expected cages
/// (or a lot outside a plan) is not compared.
/// </summary>
internal static class HarvestPlanCages
{
    public const string ExceededCode = "HARVEST_PLAN_CAGES_EXCEEDED";

    /// <summary>True when the line expects cages and the lot reaches them.</summary>
    public static bool Reached(decimal? expectedCages, decimal lotCages) => expectedCages is > 0 && lotCages >= expectedCages;

    /// <summary>A non-blocking warning when the lot holds more cages than the line expects; none otherwise.</summary>
    public static IReadOnlyList<Farming.FarmingWarning> Warnings(HarvestPlanLine? line, decimal lotCages)
    {
        if (line?.ExpectedCageCount is not > 0 || lotCages <= line.ExpectedCageCount)
        {
            return [];
        }

        var expected = line.ExpectedCageCount.Value;
        return
        [
            new Farming.FarmingWarning(
                ExceededCode,
                $"The lot holds {Format(lotCages)} cages but the harvest plan line expects {Format(expected)}.",
                new Dictionary<string, object?> { ["expectedCages"] = expected, ["lotCages"] = lotCages }),
        ];
    }

    private static string Format(decimal cages) => cages.ToString("0.##", CultureInfo.InvariantCulture);
}
