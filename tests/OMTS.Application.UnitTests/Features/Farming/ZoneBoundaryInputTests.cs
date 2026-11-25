using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Zones;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>The boundary of a zone as the app sends it (PM decision 25/11/2026): what is refused and how the rest is normalized.</summary>
public class ZoneBoundaryInputTests
{
    private static readonly double[][] Triangle = [[107.511, 21.118], [107.515, 21.118], [107.515, 21.114]];

    [Fact]
    public void An_open_ring_is_closed_rounded_to_six_decimals_and_an_altitude_is_dropped()
    {
        var (boundary, invalid) = Read(Area([[107.5110004, 21.11800049], [107.5149996, 21.118, 3.5], [107.515, 21.114]], " Khu 1 ", [" A1", "A2 ", "A3"]));

        Assert.Empty(invalid);
        var feature = Assert.Single(boundary!.Features!);
        Assert.Equal(("FeatureCollection", "Feature", "Polygon"), (boundary.Type, feature.Type, feature.Geometry!.Type));
        var ring = Assert.Single(feature.Geometry.Coordinates!);
        Assert.Equal([[107.511, 21.118], [107.515, 21.118], [107.515, 21.114], [107.511, 21.118]], ring.Select(p => p.ToArray()));
        Assert.Equal("Khu 1", feature.Properties!.Name);
        Assert.Equal(["A1", "A2", "A3"], feature.Properties.Corners!);
    }

    [Fact]
    public void A_closed_ring_stays_closed_once_and_keeps_the_order_sent()
    {
        var clockwise = Triangle.Append(Triangle[0]).Reverse().ToArray();
        var (boundary, invalid) = Read(Area(clockwise));

        Assert.Empty(invalid);
        var feature = Assert.Single(boundary!.Features!);
        Assert.Equal(clockwise, feature.Geometry!.Coordinates![0].Select(p => p.ToArray()));
        Assert.Null(feature.Properties);
    }

    [Fact]
    public void A_blank_name_is_null_and_null_clears_the_boundary()
    {
        var (boundary, invalid) = Read(Area(Triangle, "   "));
        Assert.Empty(invalid);
        Assert.Null(boundary!.Features![0].Properties);

        var check = new FieldCheck();
        Assert.Null(ZoneBoundaryInput.Read(null, check));
        Assert.True(check.IsValid);
    }

    private static readonly Dictionary<string, ZoneBoundary> RefusedShapes = new()
    {
        { "not a FeatureCollection", new ZoneBoundary("Feature", [Feature(Triangle)]) },
        { "no area", new ZoneBoundary("FeatureCollection", []) },
        { "11 areas", new ZoneBoundary("FeatureCollection", Enumerable.Repeat(Feature(Triangle), 11).ToList()) },
        { "not a Feature", new ZoneBoundary("FeatureCollection", [Feature(Triangle) with { Type = "feature" }]) },
        { "no geometry", new ZoneBoundary("FeatureCollection", [Feature(Triangle) with { Geometry = null }]) },
        { "not a Polygon", new ZoneBoundary("FeatureCollection", [Feature(Triangle) with { Geometry = new ZoneBoundaryPolygon("MultiPolygon", [Positions(Triangle)]) }]) },
        { "a hole", new ZoneBoundary("FeatureCollection", [Feature(Triangle) with { Geometry = new ZoneBoundaryPolygon("Polygon", [Positions(Triangle), Positions(Triangle)]) }]) },
        { "a position of 1 number", Area([[107.511, 21.118], [107.515], [107.515, 21.114]]) },
        { "a position of 4 numbers", Area([[107.511, 21.118], [107.515, 21.118, 0, 0], [107.515, 21.114]]) },
        { "2 corners", Area([[107.511, 21.118], [107.515, 21.118], [107.511, 21.118]]) },
        { "101 corners", Area(Enumerable.Range(0, 101).Select(i => new[] { 107.5 + i * 0.0001, 21.1 + (i % 2) * 0.0001 }).ToArray()) },
        { "a repeated corner", Area([[107.511, 21.118], [107.515, 21.118], [107.511, 21.118], [107.515, 21.114]]) },
        { "corners equal once rounded", Area([[107.511, 21.118], [107.5110001, 21.118], [107.515, 21.114]]) },
    };

    public static TheoryData<string> RefusedShapeNames()
    {
        var names = new TheoryData<string>();
        foreach (var name in RefusedShapes.Keys)
        {
            names.Add(name);
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(RefusedShapeNames))]
    public void A_shape_that_is_not_one_ring_of_3_to_100_corners_per_area_is_refused(string shape) =>
        Assert.Equal([ZoneBoundaryInput.Field], Read(RefusedShapes[shape]).Invalid);

    [Fact]
    public void One_hundred_corners_are_accepted() =>
        Assert.Empty(Read(Area(Enumerable.Range(0, 100).Select(i => new[] { 107.5 + i * 0.0001, 21.1 + (i % 2) * 0.0001 }).ToArray())).Invalid);

    [Theory]
    [InlineData(21.118, 107.511)] // latitude first: outside Vietnam
    [InlineData(0, 0)]
    [InlineData(139.0, 35.0)] // Tokyo
    public void A_corner_outside_vietnam_is_refused(double longitude, double latitude) =>
        Assert.Equal([ZoneBoundaryInput.CoordinatesField], Read(Area([[107.511, 21.118], [longitude, latitude], [107.515, 21.114]])).Invalid);

    [Fact]
    public void A_name_or_corner_labels_that_do_not_fit_are_refused()
    {
        Assert.Equal([ZoneBoundaryInput.NameField], Read(Area(Triangle, new string('k', 101))).Invalid);
        Assert.Empty(Read(Area(Triangle, new string('k', 100), ["A1", "A2", new string('a', 20)])).Invalid);
        Assert.Equal([ZoneBoundaryInput.CornersField], Read(Area(Triangle, null, ["A1", "A2"])).Invalid);
        Assert.Equal([ZoneBoundaryInput.CornersField], Read(Area(Triangle, null, ["A1", "A2", "A3", "A1"])).Invalid); // a label for the closing position
        Assert.Equal([ZoneBoundaryInput.CornersField], Read(Area(Triangle, null, ["A1", " ", "A3"])).Invalid);
        Assert.Equal([ZoneBoundaryInput.CornersField], Read(Area(Triangle, null, ["A1", "A2", new string('a', 21)])).Invalid);
    }

    [Fact]
    public void Every_invalid_part_is_named_once_across_the_areas()
    {
        var outside = Feature([[107.511, 21.118], [0, 0], [107.515, 21.114]]);
        var boundary = new ZoneBoundary("FeatureCollection", [outside, Feature(Triangle) with { Type = null }, outside, Feature(Triangle, new string('k', 101), ["A1"])]);
        Assert.Equal([ZoneBoundaryInput.CoordinatesField, ZoneBoundaryInput.Field, ZoneBoundaryInput.NameField, ZoneBoundaryInput.CornersField], Read(boundary).Invalid);
    }

    private static (ZoneBoundary? Boundary, List<string> Invalid) Read(ZoneBoundary boundary)
    {
        var check = new FieldCheck();
        return (ZoneBoundaryInput.Read(boundary, check), check.Invalid);
    }

    private static ZoneBoundary Area(double[][] positions, string? name = null, IReadOnlyList<string>? corners = null) =>
        new("FeatureCollection", [Feature(positions, name, corners)]);

    private static ZoneBoundaryFeature Feature(double[][] positions, string? name = null, IReadOnlyList<string>? corners = null) =>
        new("Feature", name is null && corners is null ? null : new ZoneBoundaryProperties(name, corners), new ZoneBoundaryPolygon("Polygon", [Positions(positions)]));

    private static IReadOnlyList<IReadOnlyList<double>> Positions(double[][] positions) => positions.Select(p => (IReadOnlyList<double>)p).ToList();
}
