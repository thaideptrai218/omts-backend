using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// The text of a mandatory SMS. It fits one SMS whatever the content: at most <see cref="MaxLength"/> characters of the
/// GSM 7-bit alphabet. Vietnamese letters lose their marks, because one Unicode character would turn the SMS into
/// UCS-2, which holds 70 characters per part and is often refused or charged more. A money type carries only its name,
/// document code and amount, so a bank account number cannot get in (decision 2). Any other type carries the MSG text,
/// built from its own fields, which read as they are.
/// <para>
/// A text over the limit gives way in one order (<see cref="NotificationSmsLayouts"/>), and the values, the allowed
/// ranges, the time and the expiry date never do: (1) the free text down to its short minimum, zone name or zone list
/// first, then raft code or license type name, then license number; (2) the closing sentence; (3) the shorter wording of
/// the water alert; (4) the free text below its minimum, in the same order. A text without a layout is cut at the end.
/// </para>
/// </summary>
public static partial class NotificationSmsText
{
    public const int MaxLength = 160;

    /// <summary>The characters a text may hold: each one is a single septet of the GSM 7-bit default alphabet.</summary>
    public const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 @$_!\"#%&'()*+,-./:;<=>?";

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
        Shorten(
            rule.AmountParameter is { } amountParameter ? ToAlphabet(DocumentCodeAndAmount(definition, amountParameter, content)) : MessageText(content.Message),
            MaxLength);

    private static string MessageText(UserMessage message)
    {
        var whole = ToAlphabet(message.Text);
        if (whole.Length <= MaxLength || NotificationSmsLayouts.Of(message) is not { } layout)
        {
            return whole;
        }

        var freeText = layout.FreeText.Select(field => ToAlphabet(field.Text)).ToList();
        var minimums = layout.FreeText.Select(field => field.Minimum).ToList();
        foreach (var form in layout.Forms)
        {
            if (FitFreeText(form, freeText, minimums) is { } fitted)
            {
                return fitted;
            }
        }

        // Only the widest values with a raft code come here: the free text goes below its minimum before a value does.
        return FitFreeText(layout.Forms[^1], freeText, freeText.Select(_ => Shortened.Length).ToList()) ?? whole;
    }

    /// <summary>The form with its free text shortened in order, or null when it does not fit with each field at its minimum.</summary>
    private static string? FitFreeText(NotificationSmsForm form, IReadOnlyList<string> freeText, IReadOnlyList<int> minimums)
    {
        var kept = freeText.ToList();
        var excess = ToAlphabet(form(kept)).Length - MaxLength;
        for (var i = 0; i < kept.Count && excess > 0; i++)
        {
            var shortened = Shorten(kept[i], Math.Max(minimums[i], kept[i].Length - excess));
            excess -= kept[i].Length - shortened.Length;
            kept[i] = shortened;
        }

        return excess > 0 ? null : ToAlphabet(form(kept));
    }

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

    /// <param name="maxLength">At least the length of <see cref="Shortened"/>.</param>
    private static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        // At a word when one ends near the limit; a very long name is cut where it stands.
        var head = text[..(maxLength - Shortened.Length)];
        var lastSpace = head.LastIndexOf(' ');
        return (lastSpace >= head.Length - Math.Min(MaxWordCut, head.Length / 2) ? head[..lastSpace] : head).TrimEnd(' ', ',', ';', ':', '.') + Shortened;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
