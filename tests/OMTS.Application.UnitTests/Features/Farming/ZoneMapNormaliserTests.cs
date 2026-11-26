using OMTS.Application.Features.Farming.Zones.BoundaryScan;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>
/// The licence map normaliser. Khu 4 is the low-resolution reading of the sample map (it may hold misreads, so it only feeds the
/// normaliser); the other polygons are made up, with areas worked out by hand.
/// </summary>
public class ZoneMapNormaliserTests
{
    private static RawMapCorner Corner(string label, string x, string y) => new(label, x, y, null, null);

    private static readonly RawMapCorner[] Khu4 =
    [
        Corner("K23", "2.315.120,32", "438.224,18"),
        Corner("K24", "2.315.073,41", "438.342,97"),
        Corner("K25", "2.314.981,76", "438.361,58"),
        Corner("K26", "2.314.939,04", "438.267,36"),
        Corner("K27", "2.315.004,71", "438.191,05"),
    ];

    [Fact]
    public void Khu_4_of_the_sample_map_computes_about_two_hectares_against_four_written_so_the_area_does_not_match()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap("VN-2000", "107°45'", [new RawMapZone("Khu 4", "(Diện tích: 4,00 ha)", Khu4)]));

        var zone = Assert.Single(reading.Zones);
        Assert.Equal(107.75, reading.CentralMeridian);
        Assert.Equal("VN-2000", reading.CoordinateSystem);
        Assert.Equal("Khu 4", zone.Name);
        Assert.Equal(4.0, zone.WrittenAreaHa);
        Assert.Equal(1.9968, zone.GridAreaHa!.Value, 3);
        Assert.False(zone.AreaMatches);
        Assert.Equal(2315120.32, zone.Corners[0].X);
        Assert.Equal(438224.18, zone.Corners[0].Y);
        Assert.Equal("K23", zone.Corners[0].Label);
    }

    [Theory]
    [InlineData(12.48, 12.5, true)] // 0.02 ha off, inside 2 % (0.25 ha)
    [InlineData(12.0, 12.5, false)] // 0.5 ha off
    [InlineData(1.04, 1.0, true)] // 0.04 ha: inside the 0.05 ha floor although it is 4 %
    [InlineData(1.06, 1.0, false)]
    [InlineData(99.9, 98.0, true)] // 1.9 ha off, just inside 2 % (1.96 ha)
    [InlineData(100.0, 98.0, false)] // 2.0 ha off, just outside
    public void Area_matches_within_two_percent_or_five_hundredths_of_a_hectare(double grid, double written, bool expected)
    {
        Assert.Equal(expected, ZoneMapNormaliser.AreaMatches(grid, written));
    }

    [Fact]
    public void Area_match_is_unknown_when_either_area_is_missing()
    {
        Assert.Null(ZoneMapNormaliser.AreaMatches(null, 4.0));
        Assert.Null(ZoneMapNormaliser.AreaMatches(4.0, null));
    }

    [Fact]
    public void A_rectangle_of_200_by_250_metres_is_five_hectares_and_matches_its_written_area()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null,
        [
            new RawMapZone("Khu 1", "(Diện tích: 5,00 ha)",
            [
                Corner("K1", "2.300.000,00", "450.000,00"), Corner("K2", "2.300.000,00", "450.250,00"),
                Corner("K3", "2.300.200,00", "450.250,00"), Corner("K4", "2.300.200,00", "450.000,00"),
            ]),
        ]));

        Assert.Equal(5.0, reading.Zones[0].GridAreaHa!.Value, 6);
        Assert.True(reading.Zones[0].AreaMatches);
    }

    [Fact]
    public void A_triangle_and_a_clockwise_ring_give_the_same_positive_area()
    {
        // Right triangle 300 m by 200 m = 3 ha, both orientations.
        var counter = new[] { Corner("A", "2.300.000", "450.000"), Corner("B", "2.300.000", "450.300"), Corner("C", "2.300.200", "450.000") };
        var clockwise = counter.Reverse().ToArray();

        var a = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null, [new RawMapZone("Khu 1", "3,00 ha", counter)])).Zones[0];
        var b = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null, [new RawMapZone("Khu 1", "3,00 ha", clockwise)])).Zones[0];

        Assert.Equal(3.0, a.GridAreaHa!.Value, 6);
        Assert.Equal(3.0, b.GridAreaHa!.Value, 6);
    }

    [Fact]
    public void A_corner_outside_the_grid_range_or_unreadable_loses_that_coordinate_and_stays_out_of_the_area()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null,
        [
            new RawMapZone("Khu 1", "(Diện tích: 1,00 ha)",
            [
                Corner("K1", "2.315.000,00", "438.000,00"),
                Corner("K2", "23.150,00", "438.100,00"), // x too small: a missing digit
                Corner("K3", "2.315.100,00", "9.438.100,00"), // y too large
                Corner("K4", "abc", "438.000,00"),
                Corner("K5", "2.315.100,00", "438.000,00"),
            ]),
        ]));

        var zone = reading.Zones[0];
        Assert.Null(zone.Corners[1].X);
        Assert.Equal(438100.0, zone.Corners[1].Y);
        Assert.Null(zone.Corners[2].Y);
        Assert.Null(zone.Corners[3].X);
        // K2, K3 and K4 lack a coordinate: a partial polygon has no area, even though K1 and K5 are fine.
        Assert.Null(zone.GridAreaHa);
        Assert.Null(zone.AreaMatches);
    }

    [Fact]
    public void One_unreadable_coordinate_leaves_the_whole_zone_without_a_grid_area_and_without_a_match()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null,
        [
            new RawMapZone("Khu 1", "(Diện tích: 1,00 ha)",
            [
                Corner("K1", "2.315.000,00", "438.000,00"), Corner("K2", "2.315.000,00", "438.100,00"),
                Corner("K3", "2.315.100,00", "438.100,00"), Corner("K4", "2.315.100,00", "không rõ"),
            ]),
        ]));

        Assert.Null(reading.Zones[0].GridAreaHa);
        Assert.Null(reading.Zones[0].AreaMatches);
        Assert.Equal(1.0, reading.Zones[0].WrittenAreaHa);
    }

    [Theory]
    [InlineData("107°45'", 107.75)]
    [InlineData("107°45' múi 3°", 107.75)] // the projection zone after it is not the meridian
    [InlineData("VN-2000, kinh tuyến trục 107°45'", 107.75)] // the 2000 of VN-2000 is not a degree
    [InlineData("kinh tuyến trục 105°30'00\"", 105.5)]
    [InlineData("107°45′", 107.75)]
    [InlineData("107°45'30\"", 107.758333)]
    [InlineData("107 độ 45 phút", 107.75)]
    [InlineData("107°", 107.0)]
    [InlineData("107.75", 107.75)]
    [InlineData("107,75", 107.75)]
    [InlineData("105°30'", 105.5)]
    public void Central_meridian_text_becomes_decimal_degrees(string text, double expected)
    {
        Assert.Equal(expected, ZoneMapNormaliser.ParseMeridian(text)!.Value, 5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("không rõ")]
    [InlineData("107°75'")] // 75 minutes
    [InlineData("57°45'")] // not a meridian of Vietnam
    [InlineData("107 45")] // numbers without their marks are ambiguous
    [InlineData("VN-2000 107")]
    [InlineData("múi 3° kinh tuyến trục 107°45'")] // the first group is 3°: not a meridian of Vietnam, so unknown rather than guessed
    [InlineData("2000")]
    public void A_central_meridian_that_is_unreadable_or_impossible_is_null(string? text)
    {
        Assert.Null(ZoneMapNormaliser.ParseMeridian(text));
    }

    [Theory]
    [InlineData("(Diện tích: 12,50 ha)", 12.5)]
    [InlineData("Diện tích: 4,00 ha", 4.0)]
    [InlineData("Khu 4 (Diện tích: 4,00 ha)", 4.0)] // the number before "ha", not the zone number
    [InlineData("1.250,5 ha", 1250.5)]
    public void The_written_area_is_the_number_before_ha(string text, double expected)
    {
        Assert.Equal(expected, ZoneMapNormaliser.ParseArea(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("(Diện tích: không rõ)")]
    [InlineData("0 ha")]
    [InlineData("12,50")] // no unit: not taken for an area
    [InlineData("Khu 1")] // the zone number is not an area
    [InlineData("Khu 1 (Diện tích: không rõ)")]
    public void A_written_area_that_is_missing_or_zero_is_null(string? text)
    {
        Assert.Null(ZoneMapNormaliser.ParseArea(text));
    }

    [Fact]
    public void Lat_and_lon_columns_are_copied_as_written_and_empty_ones_are_null()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null,
        [
            new RawMapZone("Khu 1", null,
            [
                new RawMapCorner("K1", "2.315.000,00", "438.000,00", " 20°56'31,2\"N ", "107°39'12,0\"E"),
                new RawMapCorner("K2", "2.315.000,00", "438.100,00", "", null),
            ]),
        ]));

        Assert.Equal("20°56'31,2\"N", reading.Zones[0].Corners[0].Lat);
        Assert.Equal("107°39'12,0\"E", reading.Zones[0].Corners[0].Lon);
        Assert.Null(reading.Zones[0].Corners[1].Lat);
        Assert.Null(reading.Zones[0].Corners[1].Lon);
    }

    [Fact]
    public void Zones_without_corners_are_dropped_and_the_counts_are_capped()
    {
        var many = Enumerable.Range(1, 150).Select(i => Corner($"K{i}", "2.315.000,00", "438.000,00")).ToList();
        var zones = Enumerable.Range(1, 12).Select(i => new RawMapZone($"Khu {i}", null, many)).Append(new RawMapZone("Rỗng", null, [])).ToList();

        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null, zones));

        Assert.Equal(10, reading.Zones.Count);
        Assert.All(reading.Zones, z => Assert.Equal(100, z.Corners.Count));
    }

    [Fact]
    public void A_map_with_nothing_readable_is_an_empty_reading_not_an_error()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null, null));

        Assert.Empty(reading.Zones);
        Assert.Null(reading.CentralMeridian);
        Assert.Null(reading.CoordinateSystem);
    }

    [Fact]
    public void The_info_table_location_and_written_total_area_are_read()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap(
            "VN-2000", "107°45'", [], "  Xã Đông Xá, xã Bản Sen, huyện Vân Đồn, tỉnh Quảng Ninh ", "28,50 ha"));

        Assert.Equal("Xã Đông Xá, xã Bản Sen, huyện Vân Đồn, tỉnh Quảng Ninh", reading.Location);
        Assert.Equal(28.5, reading.WrittenTotalAreaHa);
    }

    [Fact]
    public void Missing_info_rows_are_null()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap("VN-2000", "107°45'", [], "   ", null));

        Assert.Null(reading.Location);
        Assert.Null(reading.WrittenTotalAreaHa);
    }

    [Fact]
    public void A_location_over_300_characters_is_cut()
    {
        var reading = ZoneMapNormaliser.Normalise(new RawZoneMap(null, null, [], new string('a', 400), null));

        Assert.Equal(300, reading.Location!.Length);
    }

    [Theory]
    [InlineData("28,50 ha", 28.5)]
    [InlineData("28.5 ha", 28.5)]
    [InlineData("Diện tích đề nghị: 1.250,00 ha", 1250.0)]
    [InlineData("28,50", 28.5)] // the row holds nothing but the number
    [InlineData(" 28,50. ", 28.5)]
    [InlineData("100000 ha", 100000.0)]
    [InlineData("0 ha", null)]
    [InlineData("0,00", null)]
    [InlineData("100001 ha", null)]
    [InlineData("hai mươi tám ha", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_written_total_area_is_a_number_above_zero_up_to_100000_hectares(string? text, double? expected)
    {
        Assert.Equal(expected, ZoneMapNormaliser.ParseTotalArea(text));
    }
}
