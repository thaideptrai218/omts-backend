using System.Globalization;
using System.Text;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>The rules of the units of an item: the default code and the values a factor may take.</summary>
public static class InputItemUnitRules
{
    public const decimal MaxFactor = 1_000_000m;
    public const int MaxNameLength = 30;

    /// <summary>Items are coded GI000001 (seed), VT000001 (material) and HK000001 (other).</summary>
    public static string PrefixOf(InputItemGroup group) => group switch
    {
        InputItemGroup.Seed => "GI",
        InputItemGroup.Material => "VT",
        _ => "HK",
    };

    /// <summary>A factor above 0 up to <see cref="MaxFactor"/> with at most 3 decimals.</summary>
    public static bool FactorAllowed(decimal factor) => factor > 0 && factor <= MaxFactor && decimal.Round(factor, 3) == factor;

    /// <summary>The upper-case first letter of the unit name without diacritics ("thùng" is T, "đôi" is D); "U" when the name has no letter or digit.</summary>
    public static char LetterOf(string unitName)
    {
        foreach (var c in unitName.Trim())
        {
            var plain = Plain(c);
            if (char.IsAsciiLetterOrDigit(plain))
            {
                return char.ToUpperInvariant(plain);
            }
        }

        return 'U';
    }

    /// <summary>The default code of a unit: the item code, "-" and the letter of the unit name (001-C, 001-T).</summary>
    public static string DefaultCode(string itemCode, string unitName) => $"{itemCode}-{LetterOf(unitName)}";

    /// <summary>The candidate, or with a digit appended (1, 2, 3…) until <paramref name="isTaken"/> says it is free.</summary>
    public static async Task<string> FreeCodeAsync(string candidate, Func<string, Task<bool>> isTaken)
    {
        var code = candidate;
        for (var digit = 1; await isTaken(code); digit++)
        {
            code = candidate + digit.ToString(CultureInfo.InvariantCulture);
        }

        return code;
    }

    private static char Plain(char c)
    {
        if (c is 'đ' or 'Đ')
        {
            return 'd';
        }

        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        return decomposed[0];
    }
}
