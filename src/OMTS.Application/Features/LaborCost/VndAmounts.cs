using System.Globalization;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// Money rules of the labor module: whole VND, every pay line, payment and advance a multiple of 1,000. Computed
/// amounts are rounded up to the next 1,000; typed amounts must already be multiples of 1,000 (MSG41).
/// </summary>
public static class VndAmounts
{
    public const decimal Step = 1000m;

    /// <summary>37 × 2,500 = 92,500 → 93,000. Zero and negative amounts are never paid, so they give 0.</summary>
    public static decimal RoundUpToThousand(decimal amount) => amount <= 0 ? 0 : Math.Ceiling(amount / Step) * Step;

    public static bool IsThousands(decimal amount) => amount % Step == 0;

    public static bool IsWhole(decimal value) => value == decimal.Truncate(value);

    /// <summary>Quantities have at most 2 decimals (e.g. 12.75 ropes).</summary>
    public static bool HasAtMostTwoDecimals(decimal value) => value * 100 == decimal.Truncate(value * 100);

    /// <summary>"1500000" → "1,500,000", as shown in messages (MSG45).</summary>
    public static string Format(decimal amount) => amount.ToString("#,0", CultureInfo.InvariantCulture);
}
