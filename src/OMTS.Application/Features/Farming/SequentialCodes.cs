using System.Globalization;
using System.Numerics;

namespace OMTS.Application.Features.Farming;

/// <summary>
/// GBR-03 code proposals of the farming catalogue: a prefix and a number, e.g. zones KV01, KV02… The proposal is the
/// highest number already used with the prefix plus one (discontinued records included, so their codes stay reserved);
/// the user may replace it with a code of their own.
/// </summary>
public static class SequentialCodes
{
    /// <summary>Default zone code pattern (GBR-03): KV01, KV02…</summary>
    public const string ZonePrefix = "KV";

    public const int ZoneDigits = 2;

    /// <summary>The next code after <paramref name="existingCodes"/>: at least <paramref name="minDigits"/> digits, never one already used.</summary>
    public static string Next(string prefix, int minDigits, IEnumerable<string> existingCodes)
    {
        var highest = existingCodes.Select(code => NumberOf(prefix, code)).DefaultIfEmpty(BigInteger.Zero).Max();
        return prefix + (highest + 1).ToString(CultureInfo.InvariantCulture).PadLeft(minDigits, '0');
    }

    /// <summary>
    /// The number of a code of the pattern, ignoring case ("kv07" → 7), or 0 for any other code. Any number of digits
    /// counts (no overflow), so a used code is never proposed again.
    /// </summary>
    private static BigInteger NumberOf(string prefix, string code)
    {
        var trimmed = code.Trim();
        if (trimmed.Length <= prefix.Length || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return BigInteger.Zero;
        }

        var digits = trimmed[prefix.Length..];
        return digits.All(char.IsAsciiDigit) ? BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) : BigInteger.Zero;
    }
}
