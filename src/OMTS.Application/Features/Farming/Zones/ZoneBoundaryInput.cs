namespace OMTS.Application.Features.Farming.Zones;

/// <summary>
/// Checks and normalizes the boundary of a zone sent by the app (PM decision 25/11/2026). A refusal is MSG-GEN-01 naming
/// <see cref="Field"/>, <see cref="CoordinatesField"/>, <see cref="NameField"/> or <see cref="CornersField"/>, each at most once.
/// </summary>
internal static class ZoneBoundaryInput
{
    public const int MaxAreas = 10;
    public const int MinCorners = 3;
    public const int MaxCorners = 100;
    public const int MaxNameLength = 100;
    public const int MaxCornerLabelLength = 20;

    /// <summary>
    /// The shape: a FeatureCollection of 1 to 10 Features, each a Polygon of exactly one ring (no holes) of 3 to 100 distinct
    /// corners, each position 2 numbers (or 3 with an altitude).
    /// </summary>
    public const string Field = "Boundary";

    /// <summary>A corner outside Vietnam, (0, 0) included.</summary>
    public const string CoordinatesField = "Boundary coordinates";

    /// <summary>A name longer than 100 characters.</summary>
    public const string NameField = "Boundary name";

    /// <summary>Corner labels that are not one per distinct corner, or one blank or longer than 20 characters.</summary>
    public const string CornersField = "Boundary corners";

    private const int Decimals = 6;

    /// <summary>
    /// The boundary as it is stored and answered: names and labels trimmed (a blank name is null), coordinates rounded to 6
    /// decimals, an altitude dropped, an open ring closed with its first position. The ring order is the client's, never
    /// re-oriented. Null for null, which clears the boundary. The invalid parts are added to <paramref name="check"/>.
    /// </summary>
    public static ZoneBoundary? Read(ZoneBoundary? boundary, FieldCheck check)
    {
        if (boundary is null)
        {
            return null;
        }

        void Flag(string field)
        {
            if (!check.Invalid.Contains(field))
            {
                check.Invalid.Add(field);
            }
        }

        if (boundary is not { Type: "FeatureCollection", Features: { Count: >= 1 and <= MaxAreas } features })
        {
            Flag(Field);
            return null;
        }

        var areas = new List<ZoneBoundaryFeature>();
        foreach (var feature in features)
        {
            if (feature is not { Type: "Feature", Geometry: { Type: "Polygon", Coordinates: [var ring] } } || Corners(ring) is not { Count: > 0 } corners)
            {
                Flag(Field);
                continue;
            }

            if (corners.Count is < MinCorners or > MaxCorners || corners.Distinct().Count() != corners.Count)
            {
                Flag(Field);
            }

            // (0, 0) is outside the frame of Vietnam too; a value that is not finite never reaches the decimal conversion.
            if (!corners.All(c => double.IsFinite(c.Longitude) && double.IsFinite(c.Latitude) && CoordinateRules.IsInVietnam((decimal)c.Latitude, (decimal)c.Longitude)))
            {
                Flag(CoordinatesField);
            }

            var name = feature.Properties?.Name?.Trim();
            if (name is { Length: > MaxNameLength })
            {
                Flag(NameField);
            }

            var labels = feature.Properties?.Corners?.Select(l => l?.Trim() ?? string.Empty).ToList();
            if (labels is not null && (labels.Count != corners.Count || labels.Any(l => l.Length is 0 or > MaxCornerLabelLength)))
            {
                Flag(CornersField);
            }

            name = string.IsNullOrEmpty(name) ? null : name;
            var properties = name is null && labels is null ? null : new ZoneBoundaryProperties(name, labels);
            var closed = corners.Append(corners[0]).Select(c => (IReadOnlyList<double>)new[] { c.Longitude, c.Latitude }).ToList();
            areas.Add(new ZoneBoundaryFeature("Feature", properties, new ZoneBoundaryPolygon("Polygon", [closed])));
        }

        return new ZoneBoundary("FeatureCollection", areas);
    }

    /// <summary>
    /// The corners of a ring, rounded, without its closing position (the last one when it equals the first); null when the
    /// ring is missing, far too long, or holds a position that is not 2 numbers (or 3, the third an altitude, dropped).
    /// </summary>
    private static List<(double Longitude, double Latitude)>? Corners(IReadOnlyList<IReadOnlyList<double>>? ring)
    {
        if (ring is null || ring.Count > MaxCorners + 1)
        {
            return null;
        }

        var corners = new List<(double Longitude, double Latitude)>(ring.Count);
        foreach (var position in ring)
        {
            if (position is not { Count: 2 or 3 })
            {
                return null;
            }

            corners.Add((Round(position[0]), Round(position[1])));
        }

        if (corners.Count > 1 && corners[0] == corners[^1])
        {
            corners.RemoveAt(corners.Count - 1);
        }

        return corners;
    }

    private static double Round(double value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
}
