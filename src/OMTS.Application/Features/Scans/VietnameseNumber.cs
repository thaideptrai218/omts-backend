using System.Globalization;
using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Scans;

/// <summary>
/// Reads a number the way a Vietnamese document writes it ("2.315.876,23": dot between thousands, comma before the
/// decimals) and also the plain form ("2315876.23") and the English one ("2,315,876.23"). Pure; null when the text is no
/// number or the separators do not make sense. A model's number text is never trusted further than this.
/// </summary>
public static partial class VietnameseNumber
{
    /// <summary>
    /// Rules:
    /// <list type="bullet">
    /// <item>Both separators: the last one is the decimal mark; the other must group the integer part by three.</item>
    /// <item>A comma alone is the decimal mark when single ("12,50"); several commas must be thousands groups.</item>
    /// <item>A dot alone is a decimal mark ("2315876.23", "12.5"), except that a single dot followed by exactly three digits after
    /// a non-zero integer of one to three digits is thousands ("438.224" is 438224); several dots must be thousands groups.</item>
    /// </list>
    /// A trailing unit "m" and spaces are ignored; a sign or any other character gives null.
    /// </summary>
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var compact = WhiteSpace().Replace(text, "");
        if (compact.EndsWith('m') || compact.EndsWith('M'))
        {
            compact = compact[..^1];
        }

        if (!Characters().IsMatch(compact))
        {
            return null;
        }

        var lastDot = compact.LastIndexOf('.');
        var lastComma = compact.LastIndexOf(',');
        string integerPart, fraction = "";
        if (lastDot >= 0 && lastComma >= 0)
        {
            var decimalAt = Math.Max(lastDot, lastComma);
            var thousands = lastDot > lastComma ? ',' : '.';
            integerPart = compact[..decimalAt];
            fraction = compact[(decimalAt + 1)..];
            if (!IsGrouped(integerPart, thousands) || fraction.Contains(thousands) || fraction.Contains(lastDot > lastComma ? '.' : ','))
            {
                return null;
            }

            integerPart = integerPart.Replace(thousands.ToString(), "");
        }
        else if (lastComma >= 0)
        {
            if (compact.IndexOf(',') == lastComma)
            {
                (integerPart, fraction) = (compact[..lastComma], compact[(lastComma + 1)..]);
            }
            else if (IsGrouped(compact, ','))
            {
                integerPart = compact.Replace(",", "");
            }
            else
            {
                return null;
            }
        }
        else if (lastDot >= 0)
        {
            if (compact.IndexOf('.') != lastDot)
            {
                if (!IsGrouped(compact, '.'))
                {
                    return null;
                }

                integerPart = compact.Replace(".", "");
            }
            else if (SingleDotThousands().IsMatch(compact))
            {
                integerPart = compact.Replace(".", "");
            }
            else
            {
                (integerPart, fraction) = (compact[..lastDot], compact[(lastDot + 1)..]);
            }
        }
        else
        {
            integerPart = compact;
        }

        if (integerPart.Length == 0 || !Digits().IsMatch(integerPart) || (fraction.Length > 0 && !Digits().IsMatch(fraction)))
        {
            return null;
        }

        var plain = fraction.Length == 0 ? integerPart : $"{integerPart}.{fraction}";
        return double.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    }

    private static bool IsGrouped(string text, char separator) =>
        (separator == '.' ? DotGroups() : CommaGroups()).IsMatch(text);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"^[0-9.,]+$")]
    private static partial Regex Characters();

    [GeneratedRegex(@"^[0-9]+$")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^[1-9][0-9]{0,2}\.[0-9]{3}$")]
    private static partial Regex SingleDotThousands();

    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{3})+$")]
    private static partial Regex DotGroups();

    [GeneratedRegex(@"^[0-9]{1,3}(,[0-9]{3})+$")]
    private static partial Regex CommaGroups();
}
