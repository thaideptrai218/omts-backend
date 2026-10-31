using System.Globalization;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>
/// The default code pattern of input items (GBR-03): VT001, VT002… The proposal is the highest number already used
/// with this pattern plus one (discontinued items included, so their codes stay reserved); the user may replace it.
/// </summary>
public static class FarmingInputItemCodes
{
    public const string Prefix = "VT";

    /// <summary>The next code after <paramref name="existingCodes"/>: at least three digits, never one already used.</summary>
    public static string Next(IEnumerable<string> existingCodes)
    {
        var highest = existingCodes.Select(NumberOf).DefaultIfEmpty(0).Max();
        return Prefix + (highest + 1).ToString("D3", CultureInfo.InvariantCulture);
    }

    /// <summary>The number of a code of the pattern ("vt012" → 12), or 0 for any other code.</summary>
    private static int NumberOf(string code)
    {
        var digits = code.Trim();
        if (digits.Length <= Prefix.Length || !digits.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        digits = digits[Prefix.Length..];
        return digits.All(char.IsAsciiDigit) && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number < int.MaxValue
            ? number
            : 0;
    }
}
