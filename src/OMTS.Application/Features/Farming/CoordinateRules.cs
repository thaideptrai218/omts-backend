namespace OMTS.Application.Features.Farming;

/// <summary>A non-blocking remark on a saved record: the save went through, the client shows <paramref name="Message"/>.</summary>
/// <param name="Code">Stable code: <c>COORDINATES_OUTSIDE_VIETNAM</c> or <c>RAFT_FAR_FROM_ZONE</c>.</param>
public sealed record FarmingWarning(string Code, string Message, IReadOnlyDictionary<string, object?>? Details = null);

/// <summary>
/// The checks on a hand-typed or picked map pin (PM decision 11/11/2026): (0, 0) and a latitude/longitude pair given the wrong way round are refused; a pin outside Vietnam, or a raft pin
/// far from its zone, only warns. Pure functions.
/// </summary>
internal static class CoordinateRules
{
    public const double MinLatitude = 8.0;
    public const double MaxLatitude = 24.5;
    public const double MinLongitude = 102.0;
    public const double MaxLongitude = 110.0;

    /// <summary>A raft pin farther than this from its zone pin warns.</summary>
    public const double MaxRaftDistanceKm = 5.0;

    public const string OutsideVietnamCode = "COORDINATES_OUTSIDE_VIETNAM";
    public const string FarFromZoneCode = "RAFT_FAR_FROM_ZONE";

    public static bool IsInVietnam(decimal latitude, decimal longitude) =>
        (double)latitude is >= MinLatitude and <= MaxLatitude && (double)longitude is >= MinLongitude and <= MaxLongitude;

    /// <summary>Why the pair is refused, or null: (0, 0), or swapped (the given pair is not in Vietnam but the pair the other way round is).</summary>
    public static string? RefusalHint(decimal latitude, decimal longitude)
    {
        if (latitude == 0 && longitude == 0)
        {
            return "(0, 0) is not a place on the farm.";
        }

        // Longitude must also be a valid latitude for the swapped pair to be a place at all.
        return !IsInVietnam(latitude, longitude) && longitude is >= -90 and <= 90 && IsInVietnam(longitude, latitude)
            ? "Latitude and longitude look swapped: latitude is the first number (about 8 to 24 in Vietnam), longitude the second (about 102 to 110)."
            : null;
    }

    /// <summary>The great-circle distance in kilometres (haversine).</summary>
    public static double DistanceKm(decimal latitudeA, decimal longitudeA, decimal latitudeB, decimal longitudeB)
    {
        const double earthRadiusKm = 6371.0088;
        static double Rad(decimal degrees) => (double)degrees * Math.PI / 180.0;
        var (dLat, dLon) = (Rad(latitudeB - latitudeA), Rad(longitudeB - longitudeA));
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(latitudeA)) * Math.Cos(Rad(latitudeB)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * earthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public static IReadOnlyList<FarmingWarning> ForZone(decimal? latitude, decimal? longitude) =>
        latitude is { } lat && longitude is { } lon && !IsInVietnam(lat, lon)
            ? [new FarmingWarning(OutsideVietnamCode, "The pin is outside Vietnam. Check the position on the map.")]
            : [];

    public static IReadOnlyList<FarmingWarning> ForRaft(decimal? latitude, decimal? longitude, decimal? zoneLatitude, decimal? zoneLongitude)
    {
        if (latitude is not { } lat || longitude is not { } lon || zoneLatitude is not { } zoneLat || zoneLongitude is not { } zoneLon)
        {
            return [];
        }

        var km = Math.Round(DistanceKm(lat, lon, zoneLat, zoneLon), 2);
        return km > MaxRaftDistanceKm
            ? [new FarmingWarning(FarFromZoneCode, $"The raft pin is {km:0.##} km from its zone, more than {MaxRaftDistanceKm:0} km. Check the position on the map.", new Dictionary<string, object?> { ["distanceKm"] = km, ["limitKm"] = MaxRaftDistanceKm })]
            : [];
    }
}
