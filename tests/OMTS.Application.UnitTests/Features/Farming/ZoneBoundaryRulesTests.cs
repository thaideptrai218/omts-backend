using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>
/// PM decision 25/11/2026 (warn, never block): a raft pin outside the licensed boundary of its zone warns with its distance
/// to the nearest edge; a pin inside, or within 1 m of an edge, does not.
/// </summary>
public class ZoneBoundaryRulesTests
{
    // About 2.1 km east to west by 2.2 km north to south in Ha Long Bay, around the zone pin (21.05, 107.35) the API tests use.
    private static readonly IReadOnlyList<IReadOnlyList<double>> Square = Ring(107.34, 21.06, 107.36, 21.06, 107.36, 21.04, 107.34, 21.04, 107.34, 21.06);

    [Fact]
    public void A_point_inside_is_inside_whatever_the_orientation_of_the_ring()
    {
        Assert.True(ZoneBoundaryRules.Contains(Square, 107.35, 21.05));
        Assert.True(ZoneBoundaryRules.Contains(Square.Reverse().ToList(), 107.35, 21.05));
        Assert.True(ZoneBoundaryRules.Contains(Square.SkipLast(1).ToList(), 107.35, 21.05)); // an open ring too
        Assert.False(ZoneBoundaryRules.Contains(Square, 107.37, 21.05));
        Assert.False(ZoneBoundaryRules.Contains(Square, 107.35, 21.03));
    }

    [Fact]
    public void The_notch_of_a_concave_area_is_outside()
    {
        // A "U" of three by three cells of 0.01 degree, open at the top between x = 1 and x = 2.
        var u = Cells(0, 0, 3, 0, 3, 3, 2, 3, 2, 1, 1, 1, 1, 3, 0, 3, 0, 0);
        Assert.True(ZoneBoundaryRules.Contains(u, X(0.5), Y(2)));
        Assert.False(ZoneBoundaryRules.Contains(u, X(1.5), Y(2)));

        // Half a cell (0.005 degree of longitude) from both arms of the U.
        var metres = ZoneBoundaryRules.MetresOutside(Boundary(u), (decimal)Y(2), (decimal)X(1.5));
        Assert.InRange(metres!.Value, 518, 520);
    }

    [Fact]
    public void Inside_on_an_edge_or_within_one_metre_of_it_is_not_outside()
    {
        var boundary = Boundary(Square);
        Assert.Null(ZoneBoundaryRules.MetresOutside(boundary, 21.05m, 107.35m));
        Assert.Null(ZoneBoundaryRules.MetresOutside(boundary, 21.06m, 107.35m)); // on the north edge
        Assert.Null(ZoneBoundaryRules.MetresOutside(boundary, 21.06m, 107.36m)); // on a corner
        Assert.Null(ZoneBoundaryRules.MetresOutside(boundary, 21.05m, 107.360004m)); // about 0.4 m east of the east edge
        Assert.InRange(ZoneBoundaryRules.MetresOutside(boundary, 21.05m, 107.36002m)!.Value, 2, 2.2);
    }

    [Fact]
    public void The_distance_outside_is_to_the_nearest_edge_and_matches_the_great_circle()
    {
        var boundary = Boundary(Square);

        // Straight east of the east edge: the nearest point is on the edge at the same latitude.
        var east = ZoneBoundaryRules.MetresOutside(boundary, 21.05m, 107.37m)!.Value;
        Assert.InRange(east, 1037, 1039);
        Assert.InRange(east - CoordinateRules.DistanceKm(21.05m, 107.37m, 21.05m, 107.36m) * 1000, -1, 1);

        // North-east of the north-east corner: the nearest point is the corner.
        var corner = ZoneBoundaryRules.MetresOutside(boundary, 21.07m, 107.37m)!.Value;
        Assert.InRange(corner - CoordinateRules.DistanceKm(21.07m, 107.37m, 21.06m, 107.36m) * 1000, -2, 2);
    }

    [Fact]
    public void A_point_inside_any_area_is_inside_and_the_distance_is_to_the_nearest_area()
    {
        var second = Ring(107.38, 21.06, 107.40, 21.06, 107.40, 21.04, 107.38, 21.04, 107.38, 21.06);
        var boundary = Boundary(Square, second);
        Assert.Null(ZoneBoundaryRules.MetresOutside(boundary, 21.05m, 107.39m));

        // Between the two areas, a quarter of the gap from the first.
        var between = ZoneBoundaryRules.MetresOutside(boundary, 21.05m, 107.365m)!.Value;
        Assert.InRange(between, 518, 520);
    }

    [Fact]
    public void A_raft_pinned_outside_warns_with_whole_metres_and_one_without_a_pin_or_a_boundary_never_warns()
    {
        var boundary = Boundary(Square);
        var warning = Assert.Single(ZoneBoundaryRules.ForRaft(21.05m, 107.37m, boundary));
        Assert.Equal(ZoneBoundaryRules.RaftOutsideCode, warning.Code);
        Assert.Equal(1038, warning.Details!["distanceM"]);
        Assert.Contains("about 1038 m outside", warning.Message);

        Assert.Empty(ZoneBoundaryRules.ForRaft(21.05m, 107.35m, boundary));
        Assert.Empty(ZoneBoundaryRules.ForRaft(null, null, boundary));
        Assert.Empty(ZoneBoundaryRules.ForRaft(21.05m, 107.37m, null));
    }

    [Fact]
    public void A_boundary_save_names_the_rafts_pinned_outside_in_their_order()
    {
        var boundary = Boundary(Square);
        Raft[] rafts =
        [
            new() { Code = "R-A", Latitude = 21.05m, Longitude = 107.37m },
            new() { Code = "R-B", Latitude = 21.05m, Longitude = 107.35m },
            new() { Code = "R-C" },
            new() { Code = "R-D", Latitude = 21.03m, Longitude = 107.35m },
        ];

        var warning = Assert.Single(ZoneBoundaryRules.ForRafts(boundary, rafts));
        Assert.Equal(ZoneBoundaryRules.RaftsOutsideCode, warning.Code);
        Assert.Equal(2, warning.Details!["count"]);
        Assert.Equal(["R-A", "R-D"], (IEnumerable<string>)warning.Details["raftCodes"]!);

        Assert.Empty(ZoneBoundaryRules.ForRafts(boundary, [rafts[1], rafts[2]]));
        Assert.Empty(ZoneBoundaryRules.ForRafts(null, rafts));
    }

    private static double X(double cells) => 107.30 + cells * 0.01;

    private static double Y(double cells) => 21.00 + cells * 0.01;

    /// <summary>Positions from longitude, latitude pairs.</summary>
    private static List<IReadOnlyList<double>> Ring(params double[] pairs) => pairs.Chunk(2).Select(p => (IReadOnlyList<double>)p).ToList();

    /// <summary>Positions from x, y pairs counted in cells of 0.01 degree from (107.30, 21.00).</summary>
    private static List<IReadOnlyList<double>> Cells(params double[] pairs) =>
        pairs.Chunk(2).Select(p => (IReadOnlyList<double>)new[] { X(p[0]), Y(p[1]) }).ToList();

    private static ZoneBoundary Boundary(params IReadOnlyList<IReadOnlyList<double>>[] rings) =>
        new("FeatureCollection", rings.Select(r => new ZoneBoundaryFeature("Feature", null, new ZoneBoundaryPolygon("Polygon", [r]))).ToList());
}
