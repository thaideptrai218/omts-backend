namespace OMTS.Application.Features.Farming.Zones.BoundaryScan;

/// <summary>The info table and the corner tables as the model copied them: every value is the text as written, nothing is parsed or checked.</summary>
/// <param name="CoordinateSystem">e.g. "VN-2000".</param>
/// <param name="CentralMeridian">e.g. "107°45'" (kinh tuyến trục).</param>
public sealed record RawZoneMap(string? CoordinateSystem, string? CentralMeridian, IReadOnlyList<RawMapZone>? Zones);

/// <param name="Name">e.g. "Khu 1".</param>
/// <param name="WrittenArea">e.g. "(Diện tích: 12,50 ha)".</param>
public sealed record RawMapZone(string? Name, string? WrittenArea, IReadOnlyList<RawMapCorner>? Corners);

/// <param name="X">The "X (m)" column, e.g. "2.315.876,23".</param>
/// <param name="Y">The "Y (m)" column.</param>
/// <param name="Lat">The latitude column when the table has one, as written (decimal or degrees-minutes-seconds).</param>
/// <param name="Lon">The longitude column when the table has one.</param>
public sealed record RawMapCorner(string? Label, string? X, string? Y, string? Lat, string? Lon);

/// <summary>
/// <c>reading</c> of <c>GET api/zones/{zoneId}/boundary-scans/{scanId}</c>. x (northing) and y (easting) are VN-2000 grid metres;
/// the app converts them to WGS84 and the Owner reviews them before saving the boundary (<c>PUT …/boundary</c>).
/// </summary>
/// <param name="CentralMeridian">Degrees (107°45' is 107.75), or null when it could not be read.</param>
/// <param name="CoordinateSystem">As written, or null.</param>
public sealed record ZoneMapReading(double? CentralMeridian, string? CoordinateSystem, IReadOnlyList<ScannedMapZone> Zones);

/// <param name="Name">e.g. "Khu 1".</param>
/// <param name="WrittenAreaHa">The area the table title states, in hectares, or null.</param>
/// <param name="GridAreaHa">The area worked out from the corners that have both x and y (shoelace, ha); null with fewer than three.</param>
/// <param name="AreaMatches">|grid − written| ≤ max(2 % of written, 0.05 ha); null when either area is missing. A warning for the review only, never a save block.</param>
public sealed record ScannedMapZone(string? Name, double? WrittenAreaHa, double? GridAreaHa, bool? AreaMatches, IReadOnlyList<ScannedMapCorner> Corners);

/// <param name="X">Northing, grid metres, null when unreadable or outside 2.0e6…2.7e6.</param>
/// <param name="Y">Easting, grid metres, null when unreadable or outside 3.0e5…7.0e5.</param>
/// <param name="Lat">Copied as written when the table has the column, else null.</param>
/// <param name="Lon">Copied as written when the table has the column, else null.</param>
public sealed record ScannedMapCorner(string? Label, double? X, double? Y, string? Lat, string? Lon);
