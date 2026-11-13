using System.Globalization;
using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// Employee codes (LABOR-01): NV000001 from the NV document sequence when the user leaves the code empty, otherwise the code
/// they typed: ASCII letters, digits and "-", at most <see cref="MaxLength"/> characters, kept in upper case so two spellings
/// of one code cannot both be stored.
/// </summary>
public static partial class EmployeeCodes
{
    public const int MaxLength = 20;
    public const string Prefix = "NV";

    /// <summary>NV followed by at least six digits: NV000001…NV999999, then NV1000000.</summary>
    public static string Generated(long number) => $"{Prefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The typed code trimmed and in upper case: null when it is empty (the system gives one), <paramref name="valid"/> false
    /// when it has other characters or is too long.
    /// </summary>
    public static string? Typed(string? input, out bool valid)
    {
        var code = string.IsNullOrWhiteSpace(input) ? null : input.Trim().ToUpperInvariant();
        valid = code is null || TypedPattern().IsMatch(code);
        return code;
    }

    [GeneratedRegex(@"\A[A-Z0-9\-]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TypedPattern();
}
