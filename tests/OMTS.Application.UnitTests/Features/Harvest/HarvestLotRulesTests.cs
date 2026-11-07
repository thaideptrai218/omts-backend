using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>The pure rules of a lot: BR-HARVEST-03 codes, BR-HARVEST-07 figures, BR-TRACE-02 origin state, BR-CYCLE-07 every seeded lane Harvested.</summary>
public class HarvestLotRulesTests
{
    private static readonly DateOnly Date = new(2026, 10, 26);

    [Theory]
    [InlineData("KV01", "KV01-B01", "KV01-B01-20261026")]
    [InlineData("KV01", "B01", "KV01-B01-20261026")]
    [InlineData("KV01", "kv01-b01", "kv01-b01-20261026")]
    [InlineData("KV1", "KV10-B01", "KV1-KV10-B01-20261026")]
    [InlineData("KV01", "KV01", "KV01-20261026")]
    [InlineData("KV01", "KV01B01", "KV01-KV01B01-20261026")]
    public void The_base_puts_the_zone_first_unless_the_raft_code_starts_with_it(string zone, string raft, string code) =>
        Assert.Equal(code, HarvestLotCodes.Base(zone, raft, Date));

    [Theory]
    [InlineData(new string[0], "B-20261026")]
    [InlineData(new[] { "B-20261026" }, "B-20261026-2")]
    [InlineData(new[] { "B-20261026", "B-20261026-2" }, "B-20261026-3")]
    [InlineData(new[] { "B-20261026", "B-20261026-3" }, "B-20261026-4")]
    [InlineData(new[] { "b-20261026" }, "B-20261026-2")]
    [InlineData(new[] { "B-20261026-2" }, "B-20261026-3")]
    [InlineData(new[] { "B-20261026-x" }, "B-20261026")]
    [InlineData(new[] { "B-20261026-3-20261026" }, "B-20261026")]
    public void The_next_code_follows_the_highest_in_use_and_ignores_other_text(string[] existing, string next) =>
        Assert.Equal(next, HarvestLotCodes.Next("B-20261026", existing));

    // AC-09: invoiced 600, returned 300, sold 300; the remaining kg never goes below 0 (BR-HARVEST-07).
    [Theory]
    [InlineData(1000, 600, 300, 300, 0, 300, 700, 0)]
    [InlineData(1000, 600, 300, 100, 200, 300, 500, 200)]
    [InlineData(100, 600, 0, 0, 0, 600, 0, 0)]
    public void The_figures_follow_br_harvest_07(decimal estimated, decimal invoiced, decimal returned, decimal back, decimal discarded, decimal sold, decimal remaining, decimal lost)
    {
        var figures = HarvestLotFigures.Of(new HarvestLot { EstimatedKg = estimated }, new LotSalesFigures(invoiced, returned, back, discarded));

        Assert.Equal((invoiced, returned, sold, remaining, lost), (figures.InvoicedKg, figures.ReturnedKg, figures.SoldKg, figures.RemainingKg, figures.LostKg));
        Assert.Equal(estimated - sold - lost, figures.DifferenceKg);
        Assert.True(figures.NotFinal);
    }

    [Fact]
    public void The_leftover_counts_as_lost_and_a_closed_lot_is_final()
    {
        var lot = new HarvestLot { EstimatedKg = 500m, LeftoverKg = 40m, StatusId = StatusIds.HarvestLot.Closed };

        var figures = HarvestLotFigures.Of(lot, new LotSalesFigures(400m, 50m, 0m, 50m));

        Assert.Equal((350m, 90m, 60m), (figures.SoldKg, figures.LostKg, figures.DifferenceKg));
        Assert.False(figures.NotFinal);
    }

    [Theory]
    [InlineData(StatusIds.HarvestLot.Open, true)]
    [InlineData(StatusIds.HarvestLot.Reopened, true)]
    [InlineData(StatusIds.HarvestLot.Closed, false)]
    public void Open_and_reopened_lots_are_for_sale(int statusId, bool forSale) =>
        Assert.Equal(forSale, new HarvestLot { StatusId = statusId }.IsForSale);

    [Fact]
    public void A_new_lot_is_open_and_not_published()
    {
        var lot = new HarvestLot();

        Assert.Equal(StatusIds.HarvestLot.Open, lot.StatusId);
        Assert.Equal(HarvestLotOriginState.NotPublished, lot.OriginState);
    }

    [Fact]
    public void The_origin_state_is_worked_out_from_the_two_times()
    {
        var published = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

        Assert.Equal(HarvestLotOriginState.Published, new HarvestLot { PublishedAt = published }.OriginState);
        Assert.Equal(HarvestLotOriginState.Withdrawn, new HarvestLot { PublishedAt = published, WithdrawnAt = published.AddDays(1) }.OriginState);
        Assert.Equal(HarvestLotOriginState.Published, new HarvestLot { PublishedAt = published.AddDays(2), WithdrawnAt = published.AddDays(1) }.OriginState);
    }

    [Theory]
    [InlineData(new[] { LaneState.Harvested, LaneState.Harvested, LaneState.NotSeeded }, true)]
    [InlineData(new[] { LaneState.Harvested, LaneState.BeingHarvested }, false)]
    [InlineData(new[] { LaneState.Harvested, LaneState.Growing }, false)]
    [InlineData(new[] { LaneState.NotSeeded, LaneState.NotSeeded }, false)]
    [InlineData(new LaneState[0], false)]
    public void Every_seeded_lane_harvested_ignores_lanes_with_no_seed_and_needs_one_with_seed(LaneState[] lanes, bool expected) =>
        Assert.Equal(expected, LaneStateRules.EverySeededLaneHarvested(lanes));
}
