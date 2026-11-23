using System.Globalization;
using System.Text.RegularExpressions;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Infrastructure.Ocr.Paddle;

/// <summary>A text line read on the meter photo, with the recogniser's score (0…1) and its box in pixels.</summary>
public sealed record OcrLine(string Text, double Score, int Left, int Top, int Right, int Bottom)
{
    public double CenterX => (Left + Right) / 2.0;
    public double CenterY => (Top + Bottom) / 2.0;
    public int Height => Math.Max(1, Bottom - Top);
}

public enum MeterIndicator { Ph, Salinity, Temperature }

/// <summary>
/// What OMTS knows about the cooperative's meter: the decimals it shows for each indicator (to put back a decimal point the
/// OCR lost) and, optionally, the order of the unlabelled values on its display, top to bottom (e.g. pH then temperature).
/// </summary>
public sealed record MeterProfile(int PhDecimals, int SalinityDecimals, int TemperatureDecimals, IReadOnlyList<MeterIndicator> Layout)
{
    public int DecimalsOf(MeterIndicator indicator) => indicator switch
    {
        MeterIndicator.Ph => PhDecimals,
        MeterIndicator.Salinity => SalinityDecimals,
        _ => TemperatureDecimals,
    };
}

/// <summary>
/// Turns the lines read on a meter display into pH, salinity and temperature suggestions (WATER-01 step 6).
/// - A value is assigned by its label: pH; ‰, ppt, PSU, SAL; °C or a C right after the number. The label is on the
///   number's own line or on the nearest label-only line.
/// - A number with no label is assigned only through the meter's <see cref="MeterProfile.Layout"/>, in reading order.
///   Without a layout it stays unread. Value ranges overlap (8.3 can be pH or salinity, 25.2 salinity or °C), so the
///   parser never guesses the indicator from the value: the user types it instead of correcting a wrong suggestion.
/// - A guessed value gets a confidence of at most <see cref="GuessedConfidence"/>, so the app flags it for checking
///   against the photo (BR-WATER-02). Guessed means: decimal point put back, taken from the layout, or rounded to the 2
///   decimals OMTS stores.
/// - Conductivity and TDS (µS, mS, ppm), model codes stuck to letters ("pH7110") and signed numbers from printed ranges
///   ("-20°C to +70°C") are ignored.
/// </summary>
internal static partial class MeterReadingParser
{
    public const double GuessedConfidence = 0.5;

    // "45.00" is the longest display value of an indicator OMTS records.
    private const int MaxDigits = 5;

    private enum Label { None, Ph, Salinity, Temperature, Other }

    private sealed record Token(OcrLine Line, string Text, Label Label);

    // Usual values in oyster farming water: where a lost decimal point goes, and the range a layout-only value must be in.
    private static readonly Dictionary<MeterIndicator, (decimal Min, decimal Max)> Usual = new()
    {
        [MeterIndicator.Ph] = (5m, 10m),
        [MeterIndicator.Salinity] = (5m, 40m),
        [MeterIndicator.Temperature] = (10m, 40m),
    };

    public static (OcrReading<decimal>? Ph, OcrReading<decimal>? Salinity, OcrReading<decimal>? Temperature) Parse(
        IReadOnlyList<OcrLine> lines, MeterProfile meter)
    {
        var tokens = Tokens(lines);
        var found = new Dictionary<MeterIndicator, OcrReading<decimal>>();
        foreach (var token in tokens)
        {
            MeterIndicator? labelled = token.Label switch
            {
                Label.Ph => MeterIndicator.Ph,
                Label.Salinity => MeterIndicator.Salinity,
                Label.Temperature => MeterIndicator.Temperature,
                _ => null,
            };
            if (labelled is not { } indicator)
            {
                continue;
            }

            if (Read(token, indicator, meter, fromLayout: false) is { } reading
                && (!found.TryGetValue(indicator, out var current) || reading.Confidence > current.Confidence))
            {
                found[indicator] = reading;
            }
        }

        // The layout's indicators that no label gave are matched, in order, with the unlabelled values in reading order;
        // only when the counts agree, since one extra number (a clock, a code) would shift every value.
        var open = meter.Layout.Where(indicator => !found.ContainsKey(indicator)).ToList();
        var unlabelled = tokens.Where(t => t.Label is Label.None && LooksLikeAReading(t.Line.Text))
            .OrderBy(t => t.Line.CenterY).ThenBy(t => t.Line.CenterX).ToList();
        if (open.Count != unlabelled.Count)
        {
            unlabelled.Clear();
        }

        foreach (var (indicator, token) in open.Zip(unlabelled))
        {
            if (Read(token, indicator, meter, fromLayout: true) is { } reading)
            {
                found[indicator] = reading;
            }
        }

        return (found.GetValueOrDefault(MeterIndicator.Ph), found.GetValueOrDefault(MeterIndicator.Salinity), found.GetValueOrDefault(MeterIndicator.Temperature));
    }

    /// <summary>Every number that may be a display value, with the label that goes with it.</summary>
    private static List<Token> Tokens(IReadOnlyList<OcrLine> lines)
    {
        var labelLines = lines.Where(l => !NumberToken().IsMatch(l.Text) && LabelIn(l.Text) is not Label.None).ToList();
        var tokens = new List<Token>();
        foreach (var line in lines)
        {
            var matches = NumberToken().Matches(line.Text);
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var previous = match.Index > 0 ? line.Text[match.Index - 1] : ' ';
                var next = match.Index + match.Length < line.Text.Length ? line.Text[match.Index + match.Length] : ' ';
                var hasPoint = match.Value.Contains('.') || match.Value.Contains(',');
                var after = line.Text[(match.Index + match.Length)..(i + 1 < matches.Count ? matches[i + 1].Index : line.Text.Length)];
                var before = line.Text[(i > 0 ? matches[i - 1].Index + matches[i - 1].Length : 0)..match.Index];
                if ((char.IsLetter(previous) && !hasPoint) // model code ("pH7110")
                    || previous is '-' or '+' or '−' // signed, from a printed range ("-20°C to +70°C")
                    || previous is ':' or '/' || next is ':' or '/' // clock or date ("10:25", "24/11")
                    || (i > 0 && RangeEnd().IsMatch(before)) || (i + 1 < matches.Count && RangeStart().IsMatch(after)) // "5–40 °C", "0.00~14.00"
                    || match.Value.Length - (hasPoint ? 1 : 0) > MaxDigits) // a run of digits that is noise
                {
                    continue;
                }

                // A unit right after the number belongs to it ("28.6°C", "25.5 ppt"); a name right before it too ("pH 7.82",
                // "Sal 25.1"). So in "pH 7.82 Sal 25.1" the "Sal" goes with 25.1, not with 7.82.
                var label = UnitAfter(after) is var unit and not Label.None ? unit
                    : NameBefore(before) is var name and not Label.None ? name
                    : NearestLabel(line, labelLines);
                if (label is not Label.Other)
                {
                    tokens.Add(new Token(line, match.Value.Replace(',', '.'), label));
                }
            }
        }

        return tokens;
    }

    /// <summary>
    /// The value of a token for one indicator, or null when it cannot be one. With a decimal point it is taken as written.
    /// Without one, the point may have been lost: the placement in the usual range closest to the meter's decimals wins,
    /// else the meter's decimals if that is a valid value. Rounded to the 2 decimals OMTS stores.
    /// The value is a guess (flagged) when its decimals differ from the meter's (a digit or the point was lost or added),
    /// when it was rounded, or when it is outside the usual range for farm water. A value named only by the layout has no
    /// label to vouch for it, so it must be usual: a TDS "000" or a 120 °C reading never becomes a pH suggestion.
    /// </summary>
    private static OcrReading<decimal>? Read(Token token, MeterIndicator indicator, MeterProfile meter, bool fromLayout)
    {
        var guessed = fromLayout;
        var max = indicator switch { MeterIndicator.Ph => WaterValueRules.MaxPh, MeterIndicator.Salinity => WaterValueRules.MaxSalinity, _ => WaterValueRules.MaxTemperature };
        var (min, top) = Usual[indicator];
        var wanted = meter.DecimalsOf(indicator);
        decimal value;
        var point = token.Text.IndexOf('.');
        if (point >= 0)
        {
            value = decimal.Parse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            guessed |= token.Text.Length - point - 1 != wanted;
        }
        else
        {
            var digits = decimal.Parse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture);
            var placements = Enumerable.Range(0, token.Text.Length).Select(d => (Decimals: d, Value: digits / Pow10(d))).ToList();
            var usual = placements.Where(p => p.Value >= min && p.Value <= top).OrderBy(p => Math.Abs(p.Decimals - wanted));
            var meters = placements.Where(p => p.Decimals == wanted && p.Value <= max);
            if (usual.Concat(meters).Select(p => ((int Decimals, decimal Value)?)p).FirstOrDefault() is not { } chosen)
            {
                return null;
            }

            value = chosen.Value;
            guessed |= chosen.Decimals != wanted;
        }

        var unusual = value < min || value > top;
        if (value > max || (fromLayout && unusual))
        {
            return null;
        }

        var rounded = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        guessed |= unusual || rounded != value;
        var confidence = Math.Clamp(token.Line.Score, 0, 1);
        return new OcrReading<decimal>(rounded, guessed ? Math.Min(confidence, GuessedConfidence) : confidence);
    }

    private static decimal Pow10(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++)
        {
            result *= 10m;
        }

        return result;
    }

    /// <summary>A line that is mostly the number itself: a few unit characters, no words (rules out "Mi 170 Bench Meter").</summary>
    private static bool LooksLikeAReading(string text) => text.Count(char.IsLetter) <= 2;

    /// <summary>The label of the nearest label-only line, if it is close enough to belong to this number.</summary>
    private static Label NearestLabel(OcrLine line, List<OcrLine> labelLines)
    {
        var nearest = labelLines
            .Select(l => (Line: l, Distance: Math.Sqrt(Math.Pow(l.CenterX - line.CenterX, 2) + Math.Pow(l.CenterY - line.CenterY, 2))))
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        return nearest.Line is not null && nearest.Distance <= 2.5 * Math.Max(line.Height, nearest.Line.Height) ? LabelIn(nearest.Line.Text) : Label.None;
    }

    /// <summary>The label of a label-only line (no number on it), wherever the label is in the line.</summary>
    private static Label LabelIn(string text) =>
        OtherUnit().IsMatch(text) ? Label.Other
        : PhLabel().IsMatch(text) ? Label.Ph
        : SalinityLabel().IsMatch(text) ? Label.Salinity
        : text.Contains('°') || text.Contains('º') || text.Contains('℃') ? Label.Temperature
        : Label.None;

    /// <summary>The unit written right after a number: "28.6°C", "23.c", "25.5 ppt", "412 µS", "3.69 pH".</summary>
    private static Label UnitAfter(string after) => FromGroups(UnitAfterNumber().Match(after));

    /// <summary>The name written right before a number: "pH 7.82", "Sal: 25.1", "EC 1413", "Temp 28.6".</summary>
    private static Label NameBefore(string before) => FromGroups(NameBeforeNumber().Match(before));

    private static Label FromGroups(Match match) =>
        !match.Success ? Label.None
        : match.Groups["t"].Success ? Label.Temperature
        : match.Groups["s"].Success ? Label.Salinity
        : match.Groups["p"].Success ? Label.Ph
        : Label.Other;

    // ASCII digits only: \d would also take digits decimal.Parse cannot read (full-width, Arabic-Indic).
    [GeneratedRegex(@"[0-9]+(?:[.,][0-9]+)?")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"^\s*[.,]?\s*(?:(?<t>[°º℃]|c(?![a-z]))|(?<s>‰|(?:ppt|psu)(?![a-z]))|(?<o>(?:ppm|[µμu]s|ms)(?![a-z]))|(?<p>p\s?h(?![a-z])))", RegexOptions.IgnoreCase)]
    private static partial Regex UnitAfterNumber();

    [GeneratedRegex(@"(?:(?<p>(?<![a-z])p\s?h)|(?<s>(?<![a-z])sal(?:inity)?)|(?<t>(?<![a-z])temp)|(?<o>(?<![a-z])(?:ec|tds|cond)))\s*[:=]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NameBeforeNumber();

    /// <summary>The text after the first number of a printed range: "5–40", "0.00~14.00", "5 to 40".</summary>
    [GeneratedRegex(@"^\s*(?:[-–~]|to(?![a-z]))", RegexOptions.IgnoreCase)]
    private static partial Regex RangeStart();

    /// <summary>The text before the second number of a printed range.</summary>
    [GeneratedRegex(@"(?:[-–~]|(?<![a-z])to)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RangeEnd();

    [GeneratedRegex(@"(?<![a-z])p\s?h(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex PhLabel();

    [GeneratedRegex(@"‰|(?<![a-z])(?:ppt|psu|sal(?:inity)?)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex SalinityLabel();

    [GeneratedRegex(@"(?<![a-z])(?:ppm|[µμu]s|ms|tds|ec|cond)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex OtherUnit();
}
