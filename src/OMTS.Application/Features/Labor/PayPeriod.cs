using System.Globalization;

namespace OMTS.Application.Features.Labor;

/// <summary>A pay period is named after the month in which it ends: 26/09 to 25/10 is "10/2026" (BR-LABOR-13).</summary>
public static class PayPeriod
{
    public static string NameOf(DateOnly end) => end.ToString("MM/yyyy", CultureInfo.InvariantCulture);
}
