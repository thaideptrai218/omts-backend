namespace OMTS.Application.Features.Farming.Zones;

/// <summary>
/// The licensed sea area of a zone, read from the photographed map ("Sơ đồ khu vực biển đề nghị cấp phép nuôi trồng thủy sản"):
/// the server reads the corner tables of the photo (<c>POST …/boundary-scans</c>, grid coordinates as written), the app converts
/// them to WGS84 (that stays on the app, PM decision 25/11/2026) and the Owner reviews and saves the result. Written in camelCase it is RFC 7946 GeoJSON: positions
/// are [longitude, latitude].
/// </summary>
/// <param name="Type">Always "FeatureCollection".</param>
/// <param name="Features">One polygon per area ("Khu") of the map, 1 to 10.</param>
public sealed record ZoneBoundary(string? Type, IReadOnlyList<ZoneBoundaryFeature>? Features);

/// <param name="Type">Always "Feature".</param>
/// <param name="Properties">The name and corner labels of the area as the map gives them; may be null.</param>
public sealed record ZoneBoundaryFeature(string? Type, ZoneBoundaryProperties? Properties, ZoneBoundaryPolygon? Geometry);

/// <param name="Name">e.g. "Khu 1", trimmed, at most 100 characters; blank is null.</param>
/// <param name="Corners">The corner labels in ring order ("A1", "A2"…): one per distinct corner, each at most 20 characters.</param>
public sealed record ZoneBoundaryProperties(string? Name, IReadOnlyList<string>? Corners);

/// <param name="Type">Always "Polygon".</param>
/// <param name="Coordinates">
/// Exactly one ring, no holes: 3 to 100 distinct corners as [longitude, latitude] rounded to 6 decimals, closed (the first
/// position again at the end), in the order the client sent them.
/// </param>
public sealed record ZoneBoundaryPolygon(string? Type, IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? Coordinates);

/// <summary>Body of <c>PUT api/zones/{zoneId}/boundary</c>: a null <see cref="Boundary"/> clears the boundary.</summary>
public sealed record SaveZoneBoundaryRequest(ZoneBoundary? Boundary, string? RowVersion);
