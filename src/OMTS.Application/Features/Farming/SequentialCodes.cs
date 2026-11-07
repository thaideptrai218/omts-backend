using System.Globalization;
using System.Numerics;

namespace OMTS.Application.Features.Farming;

/// <summary>
/// GBR-03 code proposals of the farming catalogue: a prefix and a number, e.g. zones KV01, KV02… and rafts KV01-B01,
/// KV01-B02… The proposal is the highest number already used with the prefix plus one (discontinued records included, so
/// their codes stay reserved); the user may replace it with a code of their own. A proposal can always be saved: it fits
/// the code length and is free.
/// </summary>
public static class SequentialCodes
{
    /// <summary>Default zone code pattern (GBR-03): KV01, KV02…</summary>
    public const string ZonePrefix = "KV";

    public const int ZoneDigits = 2;

    /// <summary>The longest zone code the Code field accepts.</summary>
    public const int ZoneCodeMaxLength = 50;

    /// <summary>Default raft code pattern (GBR-03) after the zone code: KV01-B01, KV01-B02…</summary>
    public const string RaftInfix = "-B";

    public const int RaftDigits = 2;

    /// <summary>The longest raft code the Code field accepts.</summary>
    public const int RaftCodeMaxLength = 50;

    /// <summary>
    /// The next code after <paramref name="existingCodes"/>: at least <paramref name="minDigits"/> digits, at most
    /// <paramref name="maxLength"/> characters, never one already used. A used number whose next code would not fit is passed
    /// over for the highest lower one.
    /// </summary>
    public static string Next(string prefix, int minDigits, int maxLength, IEnumerable<string> existingCodes) =>
        TryNext(prefix, minDigits, maxLength, existingCodes)
        ?? throw new InvalidOperationException($"No code of the pattern {prefix} is free within {maxLength} characters.");

    /// <summary>
    /// The next raft code of a zone: {zone code}-B01 and up. A zone code too long for the pattern gives B01 and up, which
    /// still meets the rule that a raft code is unique in its zone.
    /// </summary>
    public static string NextRaft(string zoneCode, IReadOnlyCollection<string> raftCodesOfZone) =>
        TryNext(zoneCode + RaftInfix, RaftDigits, RaftCodeMaxLength, raftCodesOfZone)
        ?? Next(RaftInfix[1..], RaftDigits, RaftCodeMaxLength, raftCodesOfZone);

    private static string? TryNext(string prefix, int minDigits, int maxLength, IEnumerable<string> existingCodes)
    {
        var used = existingCodes.Select(code => NumberOf(prefix, code)).Append(BigInteger.Zero).ToHashSet();
        foreach (var number in used.OrderDescending())
        {
            var next = number + 1;
            var code = prefix + next.ToString(CultureInfo.InvariantCulture).PadLeft(minDigits, '0');
            if (code.Length <= maxLength && !used.Contains(next))
            {
                return code;
            }
        }

        return null;
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
