using System.Text.RegularExpressions;

namespace OMTS.Application.Common.Validation;

/// <summary>
/// Vietnamese phone numbers (team default, ACS shared rules): 10 digits starting with 0; "+84…" or
/// "84…" is normalised to "0…", spaces, dots and dashes are ignored.
/// </summary>
public static partial class PhoneNumbers
{
    /// <summary>The normalised number, or null when it is not a valid Vietnamese phone number.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = Separators().Replace(input.Trim(), string.Empty);
        if (digits.StartsWith("+84", StringComparison.Ordinal))
        {
            digits = "0" + digits[3..];
        }
        else if (digits.StartsWith("84", StringComparison.Ordinal) && digits.Length == 11)
        {
            digits = "0" + digits[2..];
        }

        return VietnamesePhone().IsMatch(digits) ? digits : null;
    }

    [GeneratedRegex(@"[\s.\-()]")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^0\d{9}$")]
    private static partial Regex VietnamesePhone();
}
