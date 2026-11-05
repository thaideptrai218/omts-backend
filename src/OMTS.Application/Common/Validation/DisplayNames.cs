using System.Globalization;
using System.Text;

namespace OMTS.Application.Common.Validation;

/// <summary>
/// A name as a person reads it: one name on the screen has one spelling, so the unique index of a name and any comparison
/// hold (BR-SALE-05). No SQL Server collation does this: ơ and ư compare as different from their decomposed forms in all of
/// them. Normalization form C, every run of white space (NBSP and tab included) one space, what shows nothing removed, ends
/// trimmed. A name of only invisible characters comes out empty, so it counts as missing (BR-SALE-04, BR-SALE-05).
/// </summary>
public static class DisplayNames
{
    /// <summary>The longest name of a product or a customer profile. A longer search cannot match one.</summary>
    public const int MaxLength = 200;

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (kept.Length > 0 && kept[^1] != ' ')
                {
                    kept.Append(' ');
                }
            }
            else if (!ShowsNothing(rune))
            {
                kept.Append(rune.ToString());
            }
        }

        if (kept.Length > 0 && kept[^1] == ' ')
        {
            kept.Length--;
        }

        // Last: a removed character between a letter and its mark would otherwise keep them apart.
        return kept.ToString().Normalize(NormalizationForm.FormC);
    }

    // Format and control characters (zero-width space, joiners, BOM, word joiner, soft hyphen, direction marks), and the
    // other default-ignorable code points of Unicode: combining grapheme joiner, Hangul fillers, variation selectors.
    private static bool ShowsNothing(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control
        || rune.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFFA0
            or (>= 0x180B and <= 0x180F) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);
}
