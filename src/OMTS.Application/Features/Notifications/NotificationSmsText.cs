using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// The text of a mandatory SMS. It fits one SMS whatever the content: at most <see cref="MaxLength"/> characters of the
/// GSM 7-bit alphabet. Vietnamese letters lose their marks, because one Unicode character would turn the SMS into
/// UCS-2, which holds 70 characters per part and is often refused or charged more. The text never holds a long number:
/// a money type carries only its name, document code and amount; any other type carries the MSG text with every run of
/// 8 or more digits masked, so a bank account number cannot get through by mistake.
/// </summary>
public static partial class NotificationSmsText
{
    public const int MaxLength = 160;

    /// <summary>The characters a text may hold: each one is a single septet of the GSM 7-bit default alphabet.</summary>
    public const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 @$_!\"#%&'()*+,-./:;<=>?";

    private const string Masked = "***";
    private const string Shortened = "...";
    private const int MaxWordCut = 20;

    private static readonly Dictionary<char, char> VietnameseLetters = new[]
    {
        ('a', "àáảãạăằắẳẵặâầấẩẫậ"),
        ('d', "đ"),
        ('e', "èéẻẽẹêềếểễệ"),
        ('i', "ìíỉĩị"),
        ('o', "òóỏõọôồốổỗộơờớởỡợ"),
        ('u', "ùúủũụưừứửữự"),
        ('y', "ỳýỷỹỵ"),
    }.SelectMany(group => group.Item2.Select(letter => (letter, plain: group.Item1))).ToDictionary(pair => pair.letter, pair => pair.plain);

    public static string Render(NotificationTypeDefinition definition, NotificationSmsRule rule, NotificationContent content) =>
        Shorten(rule.AmountParameter is { } amountParameter
            ? ToAlphabet(DocumentCodeAndAmount(definition, amountParameter, content))
            : LongNumber().Replace(ToAlphabet(content.Message.Text), Masked));

    private static string DocumentCodeAndAmount(NotificationTypeDefinition definition, string amountParameter, NotificationContent content)
    {
        var amount = FormatAmount(content.Message.Parameters?.GetValueOrDefault(amountParameter));
        var code = content.Record?.Code;
        return $"{definition.Name}{(code is null ? null : " " + code)}{(amount is null ? null : $": {amount} VND")}.";
    }

    private static string? FormatAmount(object? amount) => amount switch
    {
        decimal or int or long or double => Convert.ToDecimal(amount, CultureInfo.InvariantCulture).ToString("#,0", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string ToAlphabet(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            plain.Append(Translate(character));
        }

        return Spaces().Replace(plain.ToString(), " ").Trim();
    }

    private static string Translate(char character) => character switch
    {
        // Marks written as separate characters (decomposed Unicode, as some keyboards send).
        >= '̀' and <= 'ͯ' => string.Empty,
        '‰' => "ppt",
        '–' or '—' or '−' => "-",
        _ when Alphabet.Contains(character) => character.ToString(),
        _ when VietnameseLetters.TryGetValue(char.ToLowerInvariant(character), out var plain) =>
            (char.IsUpper(character) ? char.ToUpperInvariant(plain) : plain).ToString(),
        _ => " ",
    };

    private static string Shorten(string text)
    {
        if (text.Length <= MaxLength)
        {
            return text;
        }

        // At a word when one ends near the limit; a very long name is cut where it stands.
        var head = text[..(MaxLength - Shortened.Length)];
        var lastSpace = head.LastIndexOf(' ');
        return (lastSpace >= head.Length - MaxWordCut ? head[..lastSpace] : head).TrimEnd(' ', ',', ';', ':', '.') + Shortened;
    }

    // 8 or more digits, also when grouped by single spaces, dots or dashes. Dates and times are split by '/' and ':'.
    [GeneratedRegex(@"\d(?:[ .\-]?\d){7,}")]
    private static partial Regex LongNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
