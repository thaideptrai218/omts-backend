using System.Globalization;
using System.Text.RegularExpressions;
using OMTS.Application.Features.Scans;

namespace OMTS.Application.Features.Farming.Zones.BoundaryScan;

/// <summary>
/// Turns the texts the model copied from the licence map into numbers and checks them: the server computes, the model only
/// copies. Pure functions. The checks inform the review screen; none of them blocks anything.
/// </summary>
public static partial class ZoneMapNormaliser
{
    public const double MinX = 2.0e6;
    public const double MaxX = 2.7e6;
    public const double MinY = 3.0e5;
    public const double MaxY = 7.0e5;

    /// <summary>The area check: |grid − written| ≤ max(2 % of written, 0.05 ha).</summary>
    public const double AreaRelativeTolerance = 0.02;

    public const double AreaMinimumToleranceHa = 0.05;

    /// <summary>The VN-2000 central meridians of Vietnam run from 104°30' to 108°30'; the wider range only rejects nonsense.</summary>
    private const double MinMeridian = 102.0;

    private const double MaxMeridian = 111.0;

    // A map has a handful of areas ("khu") of a handful of corners; more is the model looping.
    private const int MaxZones = 10;
    private const int MaxCorners = 100;
    private const int MaxNameLength = 100;
    private const int MaxLabelLength = 20;
    private const int MaxTextLength = 50;
    private const int MaxLocationLength = 300;
    private const double MaxTotalAreaHa = 100_000;

    public static ZoneMapReading Normalise(RawZoneMap raw)
    {
        var zones = (raw.Zones ?? [])
            .Select(NormaliseZone)
            .Where(z => z.Corners.Count > 0)
            .Take(MaxZones)
            .ToList();
        return new ZoneMapReading(
            ParseMeridian(raw.CentralMeridian), Clean(raw.CoordinateSystem, MaxNameLength), zones,
            Clean(raw.Location, MaxLocationLength), ParseTotalArea(raw.WrittenTotalArea));
    }

    private static ScannedMapZone NormaliseZone(RawMapZone raw)
    {
        var corners = (raw.Corners ?? []).Take(MaxCorners).Select(NormaliseCorner).ToList();
        var written = ParseArea(raw.WrittenArea);
        var grid = GridAreaHa(corners);
        return new ScannedMapZone(Clean(raw.Name, MaxNameLength), written, grid, AreaMatches(grid, written), corners);
    }

    private static ScannedMapCorner NormaliseCorner(RawMapCorner raw) => new(
        Clean(raw.Label, MaxLabelLength),
        InRange(VietnameseNumber.Parse(raw.X), MinX, MaxX),
        InRange(VietnameseNumber.Parse(raw.Y), MinY, MaxY),
        Clean(raw.Lat, MaxTextLength),
        Clean(raw.Lon, MaxTextLength));

    /// <summary>
    /// Degrees, minutes, seconds as the info table writes them: "107°45'" is 107.75, "107°45'30\"" adds the seconds, "107°" and
    /// "107 độ 45 phút" read too. Only numbers followed by their mark count, and the first such group is the meridian, so
    /// "107°45' múi 3°" is 107.75 and "VN-2000, 107°45'" ignores the 2000. A bare "107.75" or "107,75" is read as decimal degrees.
    /// Anything else, minutes or seconds of 60 or more, or a result outside 102°…111° is null.
    /// </summary>
    public static double? ParseMeridian(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (PlainDegrees().IsMatch(trimmed))
        {
            return InMeridianRange(double.Parse(trimmed.Replace(',', '.'), CultureInfo.InvariantCulture));
        }

        var match = DegreesMinutesSeconds().Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        double Part(string group) => match.Groups[group].Success ? double.Parse(match.Groups[group].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 0;
        var (minutes, seconds) = (Part("m"), Part("s"));
        return minutes >= 60 || seconds >= 60 ? null : InMeridianRange(Part("d") + (minutes / 60) + (seconds / 3600));
    }

    private static double? InMeridianRange(double degrees) => degrees is >= MinMeridian and <= MaxMeridian ? Math.Round(degrees, 6) : null;

    /// <summary>
    /// The hectares of "(Diện tích: 12,50 ha)": the number written right before "ha". Without "ha" it is null (a first number such as
    /// the "1" of "Khu 1" must never be taken for an area), and so it is when the number is not above zero.
    /// </summary>
    public static double? ParseArea(string? text)
    {
        var match = string.IsNullOrWhiteSpace(text) ? Match.Empty : BeforeHectare().Match(text);
        return match.Success && VietnameseNumber.Parse(match.Groups["n"].Value.TrimEnd('.', ',')) is > 0 and var ha ? ha : null;
    }

    /// <summary>
    /// The hectares of the info-table row "Diện tích đề nghị" ("28,50 ha"): like <see cref="ParseArea"/>, and also a bare number
    /// ("28,50"), because the row holds nothing else. Null when it is no number, not above zero, or above 100 000 ha.
    /// </summary>
    public static double? ParseTotalArea(string? text)
    {
        var ha = ParseArea(text) ?? VietnameseNumber.Parse(text?.Trim().TrimEnd('.', ','));
        return ha is > 0 and <= MaxTotalAreaHa ? ha : null;
    }

    /// <summary>
    /// The shoelace area, in hectares, of the corners in the order given (the ring order of the table). Null unless every corner has both
    /// x and y and there are at least three: a partial polygon has no honest area. The origin is moved to the first corner first: the
    /// coordinates are millions of metres.
    /// </summary>
    public static double? GridAreaHa(IReadOnlyList<ScannedMapCorner> corners)
    {
        if (corners.Count < 3 || corners.Any(c => c.X is null || c.Y is null))
        {
            return null;
        }

        var points = corners.Select(c => (X: c.X!.Value, Y: c.Y!.Value)).ToList();

        var (x0, y0) = points[0];
        double twiceArea = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var (ax, ay) = (points[i].X - x0, points[i].Y - y0);
            var (bx, by) = (points[(i + 1) % points.Count].X - x0, points[(i + 1) % points.Count].Y - y0);
            twiceArea += (ax * by) - (bx * ay);
        }

        return Math.Abs(twiceArea) / 2 / 10_000;
    }

    /// <summary>|grid − written| ≤ max(2 % of written, 0.05 ha), on the unrounded areas; null when either is missing.</summary>
    public static bool? AreaMatches(double? gridHa, double? writtenHa) =>
        gridHa is { } grid && writtenHa is { } written
            ? Math.Abs(grid - written) <= Math.Max(AreaRelativeTolerance * written, AreaMinimumToleranceHa)
            : null;

    private static double? InRange(double? value, double min, double max) => value is { } v && v >= min && v <= max ? v : null;

    private static string? Clean(string? text, int maxLength)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    [GeneratedRegex(@"^[0-9]{3}(?:[.,][0-9]+)?$")]
    private static partial Regex PlainDegrees();

    // d° [m'] [s"]: each number is followed by its own mark; the minutes and seconds are optional.
    [GeneratedRegex(
        @"(?<d>[0-9]{1,3}(?:[.,][0-9]+)?)\s*(?:°|º|˚|độ)(?:\s*(?<m>[0-9]{1,2}(?:[.,][0-9]+)?)\s*(?:'|′|’|phút)(?:\s*(?<s>[0-9]{1,2}(?:[.,][0-9]+)?)\s*(?:''|""|″|”|’’|giây))?)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex DegreesMinutesSeconds();

    [GeneratedRegex(@"(?<n>[0-9][0-9.,]*)\s*ha\b", RegexOptions.IgnoreCase)]
    private static partial Regex BeforeHectare();
}
