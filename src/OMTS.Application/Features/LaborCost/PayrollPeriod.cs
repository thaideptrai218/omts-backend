using System.Globalization;
using System.Text.RegularExpressions;

namespace OMTS.Application.Features.LaborCost;

/// <summary>A payroll month (pay period = calendar month): key "2026-10", shown as "10/2026" in messages.</summary>
public readonly partial record struct PayrollPeriod(int Year, int Month)
{
    public DateOnly Start => new(Year, Month, 1);

    public DateOnly End => Start.AddMonths(1).AddDays(-1);

    /// <summary>"YYYY-MM", stored in <c>Payroll.Period</c> and used in routes.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");

    /// <summary>"MM/YYYY", used in user messages next to dates shown as dd/MM/yyyy.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{Month:D2}/{Year:D4}");

    public static PayrollPeriod Of(DateOnly day) => new(day.Year, day.Month);

    public bool Contains(DateOnly day) => day.Year == Year && day.Month == Month;

    /// <summary>Parses "YYYY-MM" (years 2000–2100).</summary>
    public static bool TryParse(string? text, out PayrollPeriod period)
    {
        period = default;
        var match = text is null ? Match.Empty : Pattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            return false;
        }

        period = new PayrollPeriod(year, month);
        return true;
    }

    /// <summary>Label of a stored key ("2026-10" → "10/2026"); the key itself if it cannot be read.</summary>
    public static string LabelOf(string key) => TryParse(key, out var period) ? period.Label : key;

    public override string ToString() => Key;

    [GeneratedRegex(@"\A(\d{4})-(\d{2})\z")]
    private static partial Regex Pattern();
}
