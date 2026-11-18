using System.Globalization;
using System.Text;

namespace OMTS.Application.Features.AdministrativeUnits;

public enum AddressLevel
{
    Province,
    Ward,
}

/// <summary>
/// Names of provinces and wards compared by a key: no diacritics (đ is d), lower case, punctuation as a space, no "Tỉnh / Thành phố / TP" or
/// "Phường / Xã / Đặc khu / Thị trấn" prefix (also as "P." or "X."), and no leading zeros in a number ("Phường 01" is "Phường 1").
/// </summary>
public static class AddressNames
{
    private static readonly string[][] ProvincePrefixes = [["thanh", "pho"], ["tinh"], ["tp"], ["t", "p"]];

    // The kind a ward prefix stands for, as the list writes it.
    private static readonly (string[] Words, string Kind)[] WardPrefixes =
    [
        (["dac", "khu"], "dac khu"), (["thi", "tran"], "thi tran"), (["phuong"], "phuong"), (["xa"], "xa"), (["tt"], "thi tran"), (["p"], "phuong"), (["x"], "xa"),
    ];

    /// <summary>The key of a name; empty for a blank text.</summary>
    public static string Key(string? text, AddressLevel level)
    {
        var tokens = Tokens(text);
        var skip = level == AddressLevel.Province ? ProvincePrefixLength(tokens) : WardPrefix(tokens).Length;
        return string.Join(' ', tokens.Skip(skip).Select(NumberWithoutLeadingZeros));
    }

    /// <summary>"phuong", "xa", "dac khu" or "thi tran" when the ward name starts with that prefix; otherwise null.</summary>
    public static string? WardKind(string? text) => WardPrefix(Tokens(text)).Kind;

    private static int ProvincePrefixLength(IReadOnlyList<string> tokens)
    {
        foreach (var prefix in ProvincePrefixes)
        {
            if (StartsWith(tokens, prefix))
            {
                return prefix.Length;
            }
        }

        return 0;
    }

    private static (int Length, string? Kind) WardPrefix(IReadOnlyList<string> tokens)
    {
        foreach (var (words, kind) in WardPrefixes)
        {
            if (StartsWith(tokens, words))
            {
                return (words.Length, kind);
            }
        }

        return (0, null);
    }

    // A prefix is dropped only when a name remains after it.
    private static bool StartsWith(IReadOnlyList<string> tokens, string[] prefix) =>
        tokens.Count > prefix.Length && prefix.Select((word, i) => tokens[i] == word).All(same => same);

    private static string NumberWithoutLeadingZeros(string token) =>
        token.All(char.IsAsciiDigit) ? token.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0" : token;

    private static List<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            builder.Append(lower == 'đ' ? 'd' : char.IsLetterOrDigit(lower) ? lower : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
