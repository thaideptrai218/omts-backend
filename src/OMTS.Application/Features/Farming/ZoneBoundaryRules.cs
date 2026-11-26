using OMTS.Application.Features.Farming.Zones;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming;

/// <summary>
/// Where a raft pin lies against the licensed boundary of its zone. PM decision 25/11/2026 (warn, never block), like
/// <see cref="CoordinateRules.FarFromZoneCode"/>. Inside is a ray cast on longitude and latitude; the distance to the
/// nearest edge is measured in metres on a local equirectangular projection around the pin, exact enough over the few
/// kilometres of a zone. Pure functions; positions are [longitude, latitude].
/// </summary>
internal static class ZoneBoundaryRules
{
    /// <summary>Warning of a raft save: the raft pin is outside the boundary of its zone.</summary>
    public const string RaftOutsideCode = "RAFT_OUTSIDE_ZONE_BOUNDARY";

    /// <summary>Warning of a boundary save: In Use rafts of the zone are pinned outside the new boundary.</summary>
    public const string RaftsOutsideCode = "RAFTS_OUTSIDE_ZONE_BOUNDARY";

    /// <summary>A pin this close to an edge counts as inside: the corners come off a paper map and the pin is placed by hand.</summary>
    public const double EdgeToleranceM = 1.0;

    // The mean Earth radius, as CoordinateRules.DistanceKm uses.
    private const double MetresPerDegree = 6_371_008.8 * Math.PI / 180.0;

    /// <summary><see cref="RaftOutsideCode"/> with the distance in whole metres when the pin is outside the boundary; nothing without a pin or a boundary.</summary>
    public static IReadOnlyList<FarmingWarning> ForRaft(decimal? latitude, decimal? longitude, ZoneBoundary? boundary)
    {
        if (latitude is not { } lat || longitude is not { } lon || boundary is null || MetresOutside(boundary, lat, lon) is not { } metres)
        {
            return [];
        }

        var rounded = (int)Math.Round(metres, MidpointRounding.AwayFromZero);
        return [new FarmingWarning(
            RaftOutsideCode, $"The raft pin is about {rounded} m outside the zone boundary. Check the position on the map.",
            new Dictionary<string, object?> { ["distanceM"] = rounded })];
    }

    /// <summary><see cref="RaftsOutsideCode"/> naming, in the given order, the rafts pinned outside the boundary; nothing when there is none.</summary>
    public static IReadOnlyList<FarmingWarning> ForRafts(ZoneBoundary? boundary, IEnumerable<Raft> rafts)
    {
        if (boundary is null)
        {
            return [];
        }

        var outside = rafts.Where(r => r.Latitude is { } lat && r.Longitude is { } lon && MetresOutside(boundary, lat, lon) is not null).Select(r => r.Code).ToList();
        if (outside.Count == 0)
        {
            return [];
        }

        var codes = string.Join(", ", outside);
        var message = outside.Count == 1
            ? $"Raft {codes} in use is pinned outside the zone boundary. Check its position on the map."
            : $"{outside.Count} rafts in use are pinned outside the zone boundary: {codes}. Check their positions on the map.";
        return [new FarmingWarning(RaftsOutsideCode, message, new Dictionary<string, object?> { ["count"] = outside.Count, ["raftCodes"] = outside })];
    }

    /// <summary>
    /// The pin of a zone with a boundary: the centroid of all its areas, each weighted by its own area, rounded to the 6 decimals of the
    /// columns. Taken on longitude and latitude directly: scaling an axis scales every area alike and leaves centroids where they
    /// are, so this equals the metric centroid. An area with no surface counts by the mean of its corners, and null is returned
    /// when the boundary has no ring.
    /// </summary>
    public static (decimal Latitude, decimal Longitude)? Centroid(ZoneBoundary? boundary)
    {
        if (boundary is null)
        {
            return null;
        }

        double weight = 0, weightedLon = 0, weightedLat = 0;
        double fallbackLon = 0, fallbackLat = 0;
        var rings = 0;
        foreach (var ring in Rings(boundary))
        {
            var (area, lon, lat) = RingCentroid(ring);
            weight += area;
            weightedLon += area * lon;
            weightedLat += area * lat;
            (fallbackLon, fallbackLat, rings) = (fallbackLon + lon, fallbackLat + lat, rings + 1);
        }

        if (rings == 0)
        {
            return null;
        }

        var (centreLon, centreLat) = weight > 0 ? (weightedLon / weight, weightedLat / weight) : (fallbackLon / rings, fallbackLat / rings);
        return (Math.Round((decimal)centreLat, 6, MidpointRounding.AwayFromZero), Math.Round((decimal)centreLon, 6, MidpointRounding.AwayFromZero));
    }

    /// <summary>The surface (square degrees) and the centroid of one ring; a ring with no surface answers 0 and the mean of its corners.</summary>
    private static (double Area, double Longitude, double Latitude) RingCentroid(IReadOnlyList<IReadOnlyList<double>> ring)
    {
        // Offsets from the first corner keep the products small and exact enough.
        var (x0, y0) = (ring[0][0], ring[0][1]);
        double twiceArea = 0, sumX = 0, sumY = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (xj, yj, xi, yi) = (ring[j][0] - x0, ring[j][1] - y0, ring[i][0] - x0, ring[i][1] - y0);
            var cross = xj * yi - xi * yj;
            twiceArea += cross;
            sumX += (xj + xi) * cross;
            sumY += (yj + yi) * cross;
        }

        if (twiceArea == 0)
        {
            return (0, ring.Average(p => p[0]), ring.Average(p => p[1]));
        }

        return (Math.Abs(twiceArea) / 2, x0 + sumX / (3 * twiceArea), y0 + sumY / (3 * twiceArea));
    }

    /// <summary>
    /// How far outside every area of the boundary the point is, in metres to the nearest edge; null when it is inside an area,
    /// within <see cref="EdgeToleranceM"/> of an edge, or the boundary has no ring.
    /// </summary>
    public static double? MetresOutside(ZoneBoundary boundary, decimal latitude, decimal longitude)
    {
        var (lat, lon) = ((double)latitude, (double)longitude);
        var rings = Rings(boundary);
        if (rings.Count == 0 || rings.Any(r => Contains(r, lon, lat)))
        {
            return null;
        }

        var nearest = rings.Min(r => MetresToRing(r, lon, lat));
        return nearest <= EdgeToleranceM ? null : nearest;
    }

    /// <summary>Ray casting on longitude and latitude: whether the point is inside the ring, closed or not.</summary>
    public static bool Contains(IReadOnlyList<IReadOnlyList<double>> ring, double longitude, double latitude)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (xi, yi, xj, yj) = (ring[i][0], ring[i][1], ring[j][0], ring[j][1]);
            if ((yi > latitude) != (yj > latitude) && longitude < (xj - xi) * (latitude - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>The distance in metres from the point to the nearest edge of the ring, on an equirectangular projection centred on the point.</summary>
    public static double MetresToRing(IReadOnlyList<IReadOnlyList<double>> ring, double longitude, double latitude)
    {
        var metresPerDegreeEast = MetresPerDegree * Math.Cos(latitude * Math.PI / 180.0);
        (double X, double Y) Local(IReadOnlyList<double> position) => ((position[0] - longitude) * metresPerDegreeEast, (position[1] - latitude) * MetresPerDegree);

        var nearest = double.PositiveInfinity;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            nearest = Math.Min(nearest, DistanceFromOrigin(Local(ring[j]), Local(ring[i])));
        }

        return nearest;
    }

    /// <summary>The distance from the origin of the plane to the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static double DistanceFromOrigin((double X, double Y) a, (double X, double Y) b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(-(a.X * dx + a.Y * dy) / lengthSquared, 0, 1);
        var (x, y) = (a.X + t * dx, a.Y + t * dy);
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>The ring of each area; the saved boundary always has one per area.</summary>
    private static List<IReadOnlyList<IReadOnlyList<double>>> Rings(ZoneBoundary boundary)
    {
        var rings = new List<IReadOnlyList<IReadOnlyList<double>>>();
        foreach (var feature in boundary.Features ?? [])
        {
            if (feature.Geometry?.Coordinates is [var ring, ..] && ring.Count > 0)
            {
                rings.Add(ring);
            }
        }

        return rings;
    }
}
