using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>BR-FARM-04 lane changes and the GBR-03 raft code proposal, without a database.</summary>
public class RaftRulesUnitTests
{
    private static List<Lane> Lanes(int inUse, int removed) => Enumerable.Range(1, inUse + removed)
        .Select(n => new Lane { LaneNumber = n, StatusId = n <= inUse ? StatusIds.Lane.InUse : StatusIds.Lane.Removed })
        .ToList();

    [Fact]
    public void Fewer_lanes_removes_the_last_ones_in_use_and_creates_nothing()
    {
        var change = RaftLaneChange.To(3, Lanes(5, 1));

        Assert.Equal([4, 5], change.Removed.Select(l => l.LaneNumber));
        Assert.Empty(change.Restored);
        Assert.Empty(change.NewNumbers);
    }

    [Fact]
    public void More_lanes_first_returns_the_removed_ones_in_number_order()
    {
        var change = RaftLaneChange.To(4, Lanes(2, 3));

        Assert.Equal([3, 4], change.Restored.Select(l => l.LaneNumber));
        Assert.Empty(change.NewNumbers);
        Assert.Empty(change.Removed);
    }

    [Fact]
    public void More_lanes_than_were_removed_then_creates_the_next_numbers()
    {
        var change = RaftLaneChange.To(6, Lanes(2, 2));

        Assert.Equal([3, 4], change.Restored.Select(l => l.LaneNumber));
        Assert.Equal([5, 6], change.NewNumbers);
    }

    [Fact]
    public void Same_number_changes_nothing_and_a_new_raft_starts_at_lane_one()
    {
        var same = RaftLaneChange.To(2, Lanes(2, 1));
        Assert.Empty(same.Restored.Concat(same.Removed));
        Assert.Empty(same.NewNumbers);

        Assert.Equal([1, 2, 3], RaftLaneChange.To(3, []).NewNumbers);
        Assert.Equal(2, RaftLaneChange.InUseCount(Lanes(2, 3)));
    }

    [Theory]
    [InlineData("KV01", new string[0], "KV01-B01")]
    [InlineData("KV01", new[] { "KV01-B01", "kv01-b07", "other", "KV02-B44" }, "KV01-B08")]
    [InlineData("KV01", new[] { "KV01-B99" }, "KV01-B100")]
    public void Raft_code_is_the_highest_number_of_the_zone_pattern_plus_one(string zone, string[] codes, string expected) =>
        Assert.Equal(expected, SequentialCodes.NextRaft(zone, codes));

    // Zone codes go up to 50 characters, as do raft codes: the proposal must still fit and be free.
    [Fact]
    public void A_zone_code_too_long_for_the_pattern_falls_back_to_a_short_code()
    {
        Assert.Equal("B01", SequentialCodes.NextRaft(new string('Z', 50), []));
        Assert.Equal("B03", SequentialCodes.NextRaft(new string('Z', 50), ["B01", "b02"]));

        // 46 characters + "-B" + 2 digits just fit, until 99 is used.
        var zone = new string('Z', 46);
        Assert.Equal($"{zone}-B01", SequentialCodes.NextRaft(zone, []));
        Assert.Equal("B01", SequentialCodes.NextRaft(zone, Enumerable.Range(1, 99).Select(n => $"{zone}-B{n:00}").ToList()));
    }
}
