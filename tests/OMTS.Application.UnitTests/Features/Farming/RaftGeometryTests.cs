using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>The numeric geometry of a raft (decision of 12/11/2026): the area, the legacy size text in both directions, the ranges and the soft warnings.</summary>
public class RaftGeometryTests
{
    [Theory]
    [InlineData(24, 10, 240)]
    [InlineData(24.5, 10.25, 251.13)] // 251.125 rounds half up to 2 decimals
    [InlineData(1, 1, 1)]
    public void The_area_is_length_times_width_rounded_to_two_decimals(double length, double width, double area) =>
        Assert.Equal((decimal)area, RaftGeometry.AreaOf((decimal)length, (decimal)width));

    [Fact]
    public void The_area_is_unknown_when_a_side_is_unknown()
    {
        Assert.Null(RaftGeometry.AreaOf(null, 10m));
        Assert.Null(RaftGeometry.AreaOf(10m, null));
        Assert.Null(new Raft().AreaM2);
        Assert.Equal(240m, new Raft { LengthM = 24m, WidthM = 10m }.AreaM2);
    }

    [Theory]
    [InlineData(24, 10, "24 m x 10 m")]
    [InlineData(18.5, 9.25, "18.5 m x 9.25 m")]
    [InlineData(24.00, 10.50, "24 m x 10.5 m")]
    public void The_size_text_is_written_as_length_x_width_with_a_dot(double length, double width, string text) =>
        Assert.Equal(text, RaftGeometry.SizeOf((decimal)length, (decimal)width));

    [Theory]
    [InlineData("24 m x 10 m", 24, 10)]
    [InlineData("10 m x 24 m", 10, 24)] // length first, as the web wrote it
    [InlineData(" 18.5 m x 9,25 m ", 18.5, 9.25)]
    [InlineData("24m x 10m", 24, 10)]
    public void An_older_size_text_is_read_back_into_length_and_width(string text, double length, double width) =>
        Assert.Equal(((decimal)length, (decimal)width), RaftGeometry.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("big raft")]
    [InlineData("40 x 20")]
    [InlineData("24 m x")]
    [InlineData("0 m x 10 m")] // below the range
    [InlineData("24 m x 500 m")] // above the width range
    [InlineData("501 m x 10 m")]
    [InlineData("24.555 m x 10 m")] // more than two decimals
    [InlineData("1e2 m x 10 m")]
    public void A_size_text_that_is_not_in_that_form_or_out_of_range_is_never_guessed(string? text) => Assert.Null(RaftGeometry.Parse(text));

    [Theory]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(500.01, false)]
    [InlineData(0.99, false)]
    [InlineData(24.005, false)]
    public void The_length_is_between_1_and_500_with_two_decimals(double length, bool valid) => Assert.Equal(valid, RaftGeometry.IsLengthInRange((decimal)length));

    [Theory]
    [InlineData(1, true)]
    [InlineData(200, true)]
    [InlineData(200.01, false)]
    [InlineData(0, false)]
    public void The_width_is_between_1_and_200(double width, bool valid) => Assert.Equal(valid, RaftGeometry.IsWidthInRange((decimal)width));

    [Theory]
    [InlineData(1, 24, true)]
    [InlineData(24, 24, true)]
    [InlineData(24.01, 24, false)]
    [InlineData(0.5, 24, false)]
    public void A_lane_line_is_at_least_1_m_and_at_most_the_length_of_the_raft(double line, double raft, bool valid) =>
        Assert.Equal(valid, RaftGeometry.IsLineLengthValid((decimal)line, (decimal)raft));

    private static Raft RaftOf(decimal length, decimal width, params decimal?[] lines)
    {
        var raft = new Raft { LengthM = length, WidthM = width };
        for (var i = 0; i < lines.Length; i++)
        {
            raft.Lanes.Add(new Lane { LaneNumber = i + 1, LineLengthM = lines[i] });
        }

        return raft;
    }

    [Fact]
    public void A_lane_takes_the_length_of_the_raft_unless_it_has_its_own_and_the_total_line_is_two_lines_per_lane()
    {
        var raft = RaftOf(24m, 10m, null, 18.5m, null);

        Assert.Equal([24m, 18.5m, 24m], raft.Lanes.Select(l => RaftGeometry.EffectiveLineLength(l, raft)).ToArray());
        Assert.Equal(2m * (24m + 18.5m + 24m), RaftGeometry.TotalLineLength(raft));
    }

    [Fact]
    public void The_total_line_is_unknown_when_the_length_of_the_raft_is_unknown()
    {
        var raft = new Raft { WidthM = 10m };
        raft.Lanes.Add(new Lane { LaneNumber = 1 });

        Assert.Null(RaftGeometry.TotalLineLength(raft));
    }

    [Fact]
    public void Lanes_narrower_than_a_metre_and_lanes_of_unequal_length_only_warn()
    {
        var narrow = RaftGeometry.Warnings(RaftOf(24m, 5m, null, null, null, null, null, null));
        var unequal = RaftGeometry.Warnings(RaftOf(24m, 10m, null, 18m));
        var fine = RaftGeometry.Warnings(RaftOf(24m, 10m, null, null));

        Assert.Equal([RaftGeometry.NarrowCode], narrow.Select(w => w.Code).ToArray());
        Assert.Equal([RaftGeometry.LaneLengthsDifferCode], unequal.Select(w => w.Code).ToArray());
        Assert.Empty(fine);
    }
}
