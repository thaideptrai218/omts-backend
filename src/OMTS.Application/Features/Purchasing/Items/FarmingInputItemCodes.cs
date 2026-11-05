using System.Globalization;
using System.Numerics;

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
        var highest = existingCodes.Select(NumberOf).DefaultIfEmpty(BigInteger.Zero).Max();
        return Prefix + (highest + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');
    }

    /// <summary>
    /// The number of a code of the pattern ("vt012" → 12), or 0 for any other code. Any number of digits counts (no
    /// overflow), so a used code is never proposed again (QA P1 F3).
    /// </summary>
    private static BigInteger NumberOf(string code)
    {
        var digits = code.Trim();
        if (digits.Length <= Prefix.Length || !digits.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return BigInteger.Zero;
        }

        digits = digits[Prefix.Length..];
        return digits.All(char.IsAsciiDigit) ? BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) : BigInteger.Zero;
    }
}
