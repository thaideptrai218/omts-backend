using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Application.Features.Harvest;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-04 on real sales invoices: AC-09 (with the sales return fed through the CLAIM-03 seam), sold, lost and difference kg, and the Not final mark MSG-HARVEST-04.</summary>
public class HarvestYieldAcceptanceTests
{
    private const string NotFinalText = "Some lots are still for sale, so their figures may still change.";

    // AC-09: invoice A sells the 300 kg of a lot of 300 estimated kg and closes it; all is sent back for sale (CLAIM-03: lot Reopened, 300 returned kg back for sale);
    // invoice B sells the 300 kg again: 600 invoiced, 300 returned, 300 sold, each kg counted once.
    [Fact]
    public async Task Ac_09_counts_each_kg_once_across_the_return_and_the_second_sale()
    {
        using var api = new ReturnsSeamApiFactory();
        var setup = await api.YieldSetupAsync();
        var lot = await setup.Owner.HarvestLotAsync(setup.Raft.RaftId, new HarvestFields { Cages = "6" });
        Assert.Equal(300m, lot.Kg("estimatedKg"));

        await setup.Owner.SellKgAsync(setup, lot.Id(), 300m);
        var afterA = await setup.Owner.YieldAsync();
        Assert.Equal((300m, 300m, 0m, 0m, false), afterA.YieldLot(lot.Id()).Figures());
        Assert.Equal(JsonValueKind.Null, afterA.GetProperty("notFinalNotice").ValueKind);

        await api.ReopenAsync(lot.Id(), FarmingTestKit.Today);
        api.Returns[lot.Id()] = new LotSalesFigures(0m, 300m, 300m, 0m);
        var afterReturn = await setup.Owner.YieldAsync();
        var returned = afterReturn.YieldLot(lot.Id());
        Assert.Equal((300m, 300m, 0m), (returned.Kg("invoicedKg"), returned.Kg("returnedKg"), returned.Kg("soldKg")));
        Assert.True(returned.GetProperty("notFinal").GetBoolean());
        Assert.Equal(("MSG-HARVEST-04", NotFinalText), (afterReturn.Text("notFinalNoticeCode"), afterReturn.Text("notFinalNotice")));

        await setup.Owner.SellKgAsync(setup, lot.Id(), 300m);
        var report = await setup.Owner.YieldAsync();

        var ac09 = report.YieldLot(lot.Id());
        Assert.Equal((600m, 300m, 300m), (ac09.Kg("invoicedKg"), ac09.Kg("returnedKg"), ac09.Kg("soldKg")));
        Assert.Equal((300m, 300m, 0m, 0m, false), ac09.Figures());
        Assert.Equal(("Closed", 1), (ac09.Text("status"), report.Group(setup.Raft.RaftId).GetProperty("lotCount").GetInt32()));
        Assert.Equal((300m, 300m, 0m, 0m, false), report.Group(setup.Raft.RaftId).Figures());
        Assert.Equal((300m, 300m, 0m, 0m, false), report.GetProperty("totals").Figures());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("notFinalNotice").ValueKind);
    }

    // A part sold at the port, then the Zone Manager closes the rest as spoiled (HARVEST-02): sold 120, lost 250, difference 30; not final until the close.
    [Fact]
    public async Task A_part_sale_then_a_close_with_leftover_gives_sold_lost_and_the_difference()
    {
        using var api = new ReturnsSeamApiFactory();
        var setup = await api.YieldSetupAsync();
        var manager = await api.ManagerAsync(setup.Raft.ZoneId);
        var lot = await setup.Owner.HarvestLotAsync(setup.Raft.RaftId);
        await manager.Client.SellKgAsync(setup, lot.Id(), 120m, soldWholeLot: false);

        var forSale = await manager.Client.YieldAsync();
        Assert.Equal((400m, 120m, 0m, 280m, true), forSale.Group(setup.Raft.RaftId).Figures());
        Assert.True(forSale.GetProperty("totals").GetProperty("notFinal").GetBoolean());
        Assert.Equal(("MSG-HARVEST-04", NotFinalText), (forSale.Text("notFinalNoticeCode"), forSale.Text("notFinalNotice")));

        await api.CloseLotAsync(manager.Client, lot.Id(), 250m, "Spoiled");
        var closed = await manager.Client.YieldAsync();

        Assert.Equal((400m, 120m, 250m, 30m, false), closed.Group(setup.Raft.RaftId).Figures());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("notFinalNoticeCode").ValueKind);
    }

    // A discarded return (CLAIM-03 "thrown away") is lost kg, the leftover of a later close too; a lot is never split between groups.
    [Fact]
    public async Task Discarded_returned_kg_count_as_lost_and_each_lot_stays_in_one_group()
    {
        using var api = new ReturnsSeamApiFactory();
        var setup = await api.YieldSetupAsync();
        var lot = await setup.Owner.HarvestLotAsync(setup.Raft.RaftId);
        var other = await setup.Owner.HarvestLotAsync(setup.Raft.RaftId, HarvestFields.OfLanes(2));
        await setup.Owner.SellKgAsync(setup, lot.Id(), 100m, soldWholeLot: false);
        api.Returns[lot.Id()] = new LotSalesFigures(0m, 100m, 0m, 100m);
        await api.CloseLotAsync(setup.Owner, lot.Id(), 300m, "Unsold");

        var report = await setup.Owner.YieldAsync(HarvestYieldTestKit.YieldFilters(groupBy: "zone"));

        Assert.Equal((400m, 0m, 400m, 0m, false), report.YieldLot(lot.Id()).Figures());
        var zone = Assert.Single(report.Groups());
        Assert.Equal((setup.Raft.ZoneId, 2), (zone.GetProperty("key").GetGuid(), zone.GetProperty("lotCount").GetInt32()));
        Assert.Equal((800m, 0m, 400m, 400m, true), zone.Figures());
        Assert.Equal(new[] { lot.Id(), other.Id() }.Order(), report.YieldLotIds().Order());
    }

    // The contract the web and mobile clients read.
    [Fact]
    public async Task The_report_carries_the_fields_of_scr_harvest_03()
    {
        using var api = new ReturnsSeamApiFactory();
        var setup = await api.YieldSetupAsync();
        var lot = await setup.Owner.HarvestLotAsync(setup.Raft.RaftId);

        var report = await setup.Owner.YieldAsync(HarvestYieldTestKit.YieldFilters(groupBy: "cycle"));

        Assert.Equal(
            ["from", "groupBy", "groups", "message", "messageCode", "notFinalNotice", "notFinalNoticeCode", "to", "totals"],
            report.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Cycle", report.Text("groupBy"));
        var group = Assert.Single(report.Groups());
        Assert.Equal(
            ["cycleCloseDate", "cycleId", "cycleStartDate", "cycleStatus", "differenceKg", "estimatedKg", "key", "label", "lostKg", "lotCount", "lots", "notFinal", "raftCode",
                "raftId", "soldKg", "zoneCode", "zoneId", "zoneName"],
            group.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal((setup.Raft.CycleId, "Open"), (group.GetProperty("key").GetGuid(), group.Text("cycleStatus")));
        Assert.Equal(
            ["cycleId", "differenceKg", "estimatedKg", "harvestDate", "id", "inPeriod", "invoicedKg", "lostKg", "lotCode", "notFinal", "raftCode", "raftId", "returnedKg", "soldKg",
                "status", "statusId", "zoneCode", "zoneId"],
            report.YieldLot(lot.Id()).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["differenceKg", "estimatedKg", "lostKg", "lotCount", "notFinal", "soldKg"],
            report.GetProperty("totals").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }
}
