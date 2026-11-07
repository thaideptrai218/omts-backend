using OMTS.Application.Features.Traceability.Corrections;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.UnitTests.Features.Traceability;

/// <summary>BR-TRACE-01 (TRACE-03.0.E3) and the lots whose origin information a seeding correction changes (BR-ORIGIN-03).</summary>
public sealed class TraceSeedLotRulesTests
{
    private static readonly Lane First = new() { LaneNumber = 1 }, Second = new() { LaneNumber = 2 }, Third = new() { LaneNumber = 3 };
    private static readonly Guid Lane1 = First.Id, Lane2 = Second.Id, Lane3 = Third.Id;
    private static readonly Guid LotA = Guid.NewGuid(), LotB = Guid.NewGuid();
    private static readonly DateTimeOffset Morning = new(2026, 10, 26, 2, 0, 0, TimeSpan.Zero);

    private static LotLane In(Guid lot, Guid lane, int number, int hoursLater = 0) =>
        new(lot, lot == LotA ? "KV01-B01-20261026" : "KV01-B01-20261026-2", Morning.AddHours(hoursLater), lane, number);

    [Fact]
    public void A_lane_of_a_lot_that_loses_its_last_seeding_session_is_named_the_earliest_lot_first()
    {
        LotLane[] lanes = [In(LotB, Lane1, 1, hoursLater: 3), In(LotA, Lane2, 2), In(LotA, Lane1, 1)];

        var lost = TraceSeedLotRules.LanesLosingTheirSeed(lanes, new HashSet<Guid> { Lane1, Lane2 }, lanes, new HashSet<Guid> { Lane2 });

        Assert.Equal([new LaneWithoutSeed("KV01-B01-20261026", 1), new LaneWithoutSeed("KV01-B01-20261026-2", 1)], lost);
    }

    [Fact]
    public void A_lane_with_no_seed_before_the_correction_is_not_its_doing_but_one_added_to_a_lot_without_seed_is()
    {
        LotLane[] before = [In(LotA, Lane1, 1), In(LotA, Lane3, 3)];
        LotLane[] after = [In(LotA, Lane1, 1), In(LotA, Lane3, 3), In(LotA, Lane2, 2)];

        var lost = TraceSeedLotRules.LanesLosingTheirSeed(before, new HashSet<Guid> { Lane1 }, after, new HashSet<Guid> { Lane1 });

        Assert.Equal([new LaneWithoutSeed("KV01-B01-20261026", 2)], lost);
    }

    [Fact]
    public void A_new_date_or_seed_lot_changes_every_lane_of_the_session_a_change_of_lanes_only_those_moved_and_the_quantity_none()
    {
        var before = new HashSet<Guid> { Lane1, Lane2 };
        var after = new HashSet<Guid> { Lane2, Lane3 };

        Assert.True(TraceSeedLotRules.LanesWhoseOriginChanges(before, after, dateOrSeedLotChanged: true).SetEquals([Lane1, Lane2, Lane3]));
        Assert.True(TraceSeedLotRules.LanesWhoseOriginChanges(before, after, dateOrSeedLotChanged: false).SetEquals([Lane1, Lane3]));
        Assert.Empty(TraceSeedLotRules.LanesWhoseOriginChanges(before, new HashSet<Guid>(before), dateOrSeedLotChanged: false));
    }

    [Fact]
    public void The_lots_holding_a_changed_lane_come_the_earliest_harvest_first()
    {
        var late = Lot("KV01-B01-20261027", Morning.AddDays(1), Second);
        var early = Lot("KV01-B01-20261026", Morning, First, Second);
        var other = Lot("KV01-B01-20261028", Morning.AddDays(2), Third);

        Assert.Equal([early, late], TraceSeedLotRules.LotsHolding([late, other, early], new HashSet<Guid> { Lane2 }));
        Assert.Empty(TraceSeedLotRules.LotsHolding([late, other, early], new HashSet<Guid>()));
    }

    [Fact]
    public void Msg_trace_03_names_the_first_lane_and_lists_them_all()
    {
        var refusal = TraceSeedLotRules.Refusal([new LaneWithoutSeed("KV01-B01-20261026", 4), new LaneWithoutSeed("KV01-B01-20261027", 5)]);

        Assert.Equal("MSG-TRACE-03", refusal.Message.Code);
        Assert.Equal("The correction is not saved: lane 4 of harvest lot KV01-B01-20261026 would have no seed lot. Please keep at least one seed lot for this lane.", refusal.Message.Text);
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<LaneWithoutSeed>>(refusal.Details!["lanes"]).Count);
    }

    private static HarvestLot Lot(string code, DateTimeOffset at, params Lane[] lanes)
    {
        var lot = new HarvestLot { LotCode = code, HarvestedAt = at };
        foreach (var lane in lanes)
        {
            lot.Lanes.Add(new HarvestLotLane { LotId = lot.Id, LaneId = lane.Id, Lane = lane });
        }

        return lot;
    }
}
