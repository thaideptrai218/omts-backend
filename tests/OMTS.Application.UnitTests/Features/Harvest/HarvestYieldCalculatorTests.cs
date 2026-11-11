using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>HARVEST-04 figures (BR-HARVEST-07): the AC-09 vector, lost kg and the difference, the Not final marks, exact sums and the three groupings.</summary>
public class HarvestYieldCalculatorTests
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 31);

    private static readonly FarmingZone ZoneA = new() { Code = "KV01", Name = "Zone A" };
    private static readonly FarmingZone ZoneB = new() { Code = "KV02", Name = "Zone B" };
    private static readonly Raft RaftA1 = RaftOf(ZoneA, "KV01-B01");
    private static readonly Raft RaftA2 = RaftOf(ZoneA, "KV01-B02");
    private static readonly Raft RaftB1 = RaftOf(ZoneB, "KV02-B01");

    // AC-09: invoice A sells the 300 kg (lot closed by the tick, leftover 0), all sent back for sale, invoice B sells them again: 600 invoiced, 300 returned, 300 sold.
    [Fact]
    public void Ac_09_counts_each_kg_once()
    {
        var cycle = CycleOf(RaftA1, new DateOnly(2026, 8, 1));
        var lot = LotOf(cycle, 300m, new DateOnly(2026, 10, 26), StatusIds.HarvestLot.Closed, leftoverKg: 0m);
        var sales = new Dictionary<Guid, LotSalesFigures> { [lot.Id] = new(600m, 300m, 300m, 0m) };

        var result = Build([lot], sales, HarvestYieldGrouping.Raft);

        var view = Assert.Single(Assert.Single(result.Groups).Lots);
        Assert.Equal((600m, 300m, 300m, 0m, 0m, false), (view.InvoicedKg, view.ReturnedKg, view.SoldKg, view.LostKg, view.DifferenceKg, view.NotFinal));
        Assert.Equal(new HarvestYieldTotals(1, 300m, 300m, 0m, 0m, false), result.Totals);
        // Sold again on a lot still for sale: the same kg, and the lot is not final yet.
        var reopened = LotOf(cycle, 300m, new DateOnly(2026, 10, 26), StatusIds.HarvestLot.Reopened);
        var stillForSale = Build([reopened], new Dictionary<Guid, LotSalesFigures> { [reopened.Id] = new(600m, 300m, 300m, 0m) }, HarvestYieldGrouping.Raft);
        Assert.Equal((300m, 0m, 0m, true), (stillForSale.Totals.SoldKg, stillForSale.Totals.LostKg, stillForSale.Totals.DifferenceKg, stillForSale.Totals.NotFinal));
    }

    // Lost kg = leftover at the close + discarded kg; difference = estimated - sold - lost, below 0 when more was sold than the cage count gave.
    [Fact]
    public void Lost_kg_and_the_difference_follow_br_harvest_07()
    {
        var cycle = CycleOf(RaftA1, new DateOnly(2026, 8, 1));
        var closed = LotOf(cycle, 400m, new DateOnly(2026, 10, 5), StatusIds.HarvestLot.Closed, leftoverKg: 12.5m);
        var oversold = LotOf(cycle, 400m, new DateOnly(2026, 10, 6), StatusIds.HarvestLot.Closed, leftoverKg: 0m);
        var sales = new Dictionary<Guid, LotSalesFigures>
        {
            [closed.Id] = new(350m, 40m, 32.5m, 7.5m),
            [oversold.Id] = new(420.25m, 0m, 0m, 0m),
        };

        var lots = Build([closed, oversold], sales, HarvestYieldGrouping.Raft).Groups.Single().Lots;

        Assert.Equal((310m, 20m, 70m), (lots[0].SoldKg, lots[0].LostKg, lots[0].DifferenceKg));
        Assert.Equal((420.25m, 0m, -20.25m), (lots[1].SoldKg, lots[1].LostKg, lots[1].DifferenceKg));
    }

    // A lot of a group still for sale (Open or Reopened) makes the group and the totals Not final (MSG-HARVEST-04); a group of Closed lots is final.
    [Theory]
    [InlineData(StatusIds.HarvestLot.Open, true)]
    [InlineData(StatusIds.HarvestLot.Reopened, true)]
    [InlineData(StatusIds.HarvestLot.Closed, false)]
    public void A_group_is_not_final_while_one_of_its_lots_is_for_sale(int secondStatus, bool notFinal)
    {
        var cycle = CycleOf(RaftA1, new DateOnly(2026, 8, 1));
        var closedOther = LotOf(CycleOf(RaftA2, new DateOnly(2026, 8, 1)), 100m, new DateOnly(2026, 10, 2), StatusIds.HarvestLot.Closed, leftoverKg: 0m);
        var lots = new[]
        {
            LotOf(cycle, 100m, new DateOnly(2026, 10, 2), StatusIds.HarvestLot.Closed, leftoverKg: 0m),
            LotOf(cycle, 100m, new DateOnly(2026, 10, 3), secondStatus, leftoverKg: secondStatus == StatusIds.HarvestLot.Closed ? 0m : null),
            closedOther,
        };

        var result = Build(lots, new(), HarvestYieldGrouping.Raft);

        Assert.Equal([notFinal, false], result.Groups.Select(g => g.NotFinal));
        Assert.Equal(notFinal, result.Totals.NotFinal);
        Assert.Equal([false, notFinal], result.Groups[0].Lots.Select(l => l.NotFinal));
    }

    // Kg with three decimals add up exactly: no float and no rounding anywhere.
    [Fact]
    public void Sums_are_exact_and_never_rounded()
    {
        var cycle = CycleOf(RaftA1, new DateOnly(2026, 8, 1));
        var a = LotOf(cycle, 0.1m, new DateOnly(2026, 10, 2), StatusIds.HarvestLot.Closed, leftoverKg: 0.005m);
        var b = LotOf(cycle, 0.2m, new DateOnly(2026, 10, 3), StatusIds.HarvestLot.Closed, leftoverKg: 0.001m);
        var sales = new Dictionary<Guid, LotSalesFigures> { [a.Id] = new(0.105m, 0m, 0m, 0m), [b.Id] = new(0.205m, 0m, 0m, 0m) };

        var totals = Build([a, b], sales, HarvestYieldGrouping.Zone).Totals;

        Assert.Equal((0.3m, 0.31m, 0.006m, -0.016m), (totals.EstimatedKg, totals.SoldKg, totals.LostKg, totals.DifferenceKg));
    }

    // POST-1: each figure of a group is the total of its lots; the zone of a lot is the zone of its raft; groups by zone code, raft code, cycle start.
    [Fact]
    public void The_three_groupings_total_their_lots_in_order()
    {
        var a1Old = CycleOf(RaftA1, new DateOnly(2026, 3, 1), closed: true);
        var a1 = CycleOf(RaftA1, new DateOnly(2026, 9, 1));
        var a2 = CycleOf(RaftA2, new DateOnly(2026, 9, 1));
        var b1 = CycleOf(RaftB1, new DateOnly(2026, 9, 1));
        var lots = new[]
        {
            LotOf(b1, 50m, new DateOnly(2026, 10, 9), StatusIds.HarvestLot.Open),
            LotOf(a2, 30m, new DateOnly(2026, 10, 8), StatusIds.HarvestLot.Closed, leftoverKg: 0m),
            LotOf(a1, 20m, new DateOnly(2026, 10, 7), StatusIds.HarvestLot.Closed, leftoverKg: 0m),
            LotOf(a1Old, 10m, new DateOnly(2026, 6, 1), StatusIds.HarvestLot.Closed, leftoverKg: 1m),
        };

        var byRaft = Build(lots, new(), HarvestYieldGrouping.Raft).Groups;
        var byZone = Build(lots, new(), HarvestYieldGrouping.Zone).Groups;
        var byCycle = Build(lots, new(), HarvestYieldGrouping.Cycle).Groups;

        Assert.Equal([("KV01-B01", 2, 30m), ("KV01-B02", 1, 30m), ("KV02-B01", 1, 50m)], byRaft.Select(g => (g.Label, g.LotCount, g.EstimatedKg)));
        Assert.Equal([RaftA1.Id, RaftA2.Id, RaftB1.Id], byRaft.Select(g => g.Key));
        Assert.Equal(["2026-06-01", "2026-10-07"], byRaft[0].Lots.Select(l => l.HarvestDate.ToString("yyyy-MM-dd")));
        Assert.Equal([false, true], byRaft[0].Lots.Select(l => l.InPeriod));

        Assert.Equal([("KV01 - Zone A", 3, 60m, 1m, false), ("KV02 - Zone B", 1, 50m, 0m, true)], byZone.Select(g => (g.Label, g.LotCount, g.EstimatedKg, g.LostKg, g.NotFinal)));
        Assert.All(byZone, g => Assert.Null(g.RaftId));
        Assert.All(byZone, g => Assert.Null(g.CycleId));

        Assert.Equal(["KV01-B01 from 2026-03-01", "KV01-B01 from 2026-09-01", "KV01-B02 from 2026-09-01", "KV02-B01 from 2026-09-01"], byCycle.Select(g => g.Label));
        Assert.Equal((a1Old.Id, "Closed", (DateOnly?)new DateOnly(2026, 6, 30)), (byCycle[0].Key, byCycle[0].CycleStatus, byCycle[0].CycleCloseDate));
        Assert.Equal(("Open", RaftA1.Code), (byCycle[1].CycleStatus, byCycle[1].RaftCode));
        Assert.Equal(new HarvestYieldTotals(4, 110m, 0m, 1m, 109m, true), Build(lots, new(), HarvestYieldGrouping.Cycle).Totals);
    }

    [Fact]
    public void No_lot_gives_no_group_and_zero_totals()
    {
        var result = Build([], new(), HarvestYieldGrouping.Cycle);

        Assert.Empty(result.Groups);
        Assert.Equal(new HarvestYieldTotals(0, 0m, 0m, 0m, 0m, false), result.Totals);
    }

    // HARVEST-04.2: the figures shown with the Not final marks, a total row, and the lots of each group.
    [Fact]
    public void The_export_holds_the_groups_with_a_total_row_and_the_lots_with_their_marks()
    {
        var cycle = CycleOf(RaftA1, new DateOnly(2026, 9, 1));
        var open = LotOf(cycle, 400m, new DateOnly(2026, 10, 26), StatusIds.HarvestLot.Open, code: "KV01-B01-20261026");
        var sales = new Dictionary<Guid, LotSalesFigures> { [open.Id] = new(120m, 0m, 0m, 0m) };
        var result = Build([open], sales, HarvestYieldGrouping.Raft);
        var report = new HarvestYieldReport(From, To, HarvestYieldGrouping.Raft, result.Groups, result.Totals, null, null, null, null);

        var sheets = HarvestYieldExport.Sheets(report);

        Assert.Equal("yield-raft-20261001-20261031.xlsx", HarvestYieldExport.FileName(report));
        Assert.Equal(["Groups", "Lots"], sheets.Select(s => s.Name));
        Assert.Equal(
            new[]
            {
                new object?[] { "KV01-B01", "KV01", 1, 400m, 120m, 0m, 280m, "Not final", 0m },
                new object?[] { "Total", null, 1, 400m, 120m, 0m, 280m, "Not final", 0m },
            },
            sheets[0].Rows.Select(r => r.ToArray()));
        Assert.Equal(
            new object?[] { "KV01-B01", "KV01-B01-20261026", "KV01", "KV01-B01", new DateOnly(2026, 10, 26), "Open", 400m, 120m, 0m, 120m, 0m, 280m, "Not final", "Yes", 0m },
            Assert.Single(sheets[1].Rows));
        Assert.Equal(sheets[0].Headers.Count, sheets[0].Rows[0].Count);
        Assert.Equal(sheets[1].Headers.Count, sheets[1].Rows[0].Count);
    }

    private static HarvestYieldResult Build(IReadOnlyList<HarvestLot> lots, Dictionary<Guid, LotSalesFigures> sales, HarvestYieldGrouping groupBy) =>
        HarvestYieldCalculator.Build(lots, sales, groupBy, From, To, StatusName);

    private static string StatusName(int statusId) => statusId switch
    {
        StatusIds.HarvestLot.Open or StatusIds.CultivationCycle.Open => "Open",
        StatusIds.HarvestLot.Reopened => "Reopened",
        _ => "Closed",
    };

    private static Raft RaftOf(FarmingZone zone, string code) => new() { Zone = zone, ZoneId = zone.Id, Code = code };

    private static CultivationCycle CycleOf(Raft raft, DateOnly start, bool closed = false) => new()
    {
        Raft = raft,
        RaftId = raft.Id,
        StartDate = start,
        StatusId = closed ? StatusIds.CultivationCycle.Closed : StatusIds.CultivationCycle.Open,
        CloseDate = closed ? new DateOnly(2026, 6, 30) : null,
    };

    private static HarvestLot LotOf(CultivationCycle cycle, decimal estimatedKg, DateOnly harvested, int statusId, decimal? leftoverKg = null, string? code = null) => new()
    {
        LotCode = code ?? $"{cycle.Raft.Code}-{harvested:yyyyMMdd}-{Guid.NewGuid():N}",
        Cycle = cycle,
        CycleId = cycle.Id,
        Raft = cycle.Raft,
        RaftId = cycle.RaftId,
        HarvestDate = harvested,
        HarvestedAt = new DateTimeOffset(harvested.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(7)),
        EstimatedKg = estimatedKg,
        StatusId = statusId,
        LeftoverKg = leftoverKg,
    };
}
