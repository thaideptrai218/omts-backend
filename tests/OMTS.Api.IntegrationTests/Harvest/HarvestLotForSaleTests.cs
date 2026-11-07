using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>What SALE-05 chooses from (step 1, SCR-SALE-04) and the open harvest lots of the dashboard (FARM-06, BR-FARM-10): Open and Reopened lots, in the zones of the caller.</summary>
public class HarvestLotForSaleTests
{
    private static Task CloseAsync(AuthApiFactory api, Guid lotId) => api.UpdateAsync<HarvestLot>(lotId, l =>
    {
        l.StatusId = StatusIds.HarvestLot.Closed;
        l.CloseDate = FarmingTestKit.Today;
        l.LeftoverKg = 0m;
    });

    [Fact]
    public async Task The_list_holds_the_open_and_reopened_lots_with_what_sale_05_shows_and_the_latest_harvest_first()
    {
        using var api = new SalesDoubleApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var closed = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var reopened = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        var open = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["3"], Cages = "10" });
        await CloseAsync(api, closed.Id());
        await api.UpdateAsync<HarvestLot>(reopened.Id(), l =>
        {
            l.StatusId = StatusIds.HarvestLot.Reopened;
            l.ReopenedOn = FarmingTestKit.Today;
        });
        api.Sales.Figures[open.Id()] = new LotSalesFigures(120m, 20m, 5m, 15m);

        var list = await owner.LotsForSaleAsync();

        Assert.Equal([open.Text("lotCode"), reopened.Text("lotCode")], list.ForSaleCodes());
        Assert.Equal(JsonNull, list.GetProperty("message").ValueKind);
        var first = list.GetProperty("lots")[0];
        Assert.Equal(open.Id(), first.Id());
        Assert.Equal(raft.RaftId, first.GetProperty("raftId").GetGuid());
        Assert.Equal(open.Text("raftCode"), first.Text("raftCode"));
        Assert.Equal(raft.ZoneId, first.GetProperty("zoneId").GetGuid());
        Assert.Equal(open.Text("zoneCode"), first.Text("zoneCode"));
        Assert.False(string.IsNullOrEmpty(first.Text("zoneName")));
        Assert.Equal(open.Date("harvestDate"), first.Date("harvestDate"));
        Assert.Equal(385m, first.Kg("remainingKg")); // 500 - 120 + 5, for information only (BR-HARVEST-02).
        Assert.Equal((2901, "Open"), (first.GetProperty("statusId").GetInt32(), first.Text("status")));
        Assert.Equal(("Reopened", 2902), (list.GetProperty("lots")[1].Text("status"), list.GetProperty("lots")[1].GetProperty("statusId").GetInt32()));

        // Never on this list: anything of the kg per cage, the photos or the recorder that SALE-05 has no use for.
        Assert.Equal(
            ["harvestDate", "id", "lotCode", "originState", "raftCode", "raftId", "remainingKg", "status", "statusId", "withdrawnAt", "zoneCode", "zoneId", "zoneName"],
            first.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    // BR-TRACE-06 and MSG-SALE-09: a Withdrawn lot can still be chosen, with its date for the warning; the reason stays internal. BR-HARVEST-04: selling goes on after the close of the cycle.
    [Fact]
    public async Task A_withdrawn_lot_shows_its_date_a_lot_of_a_closed_cycle_stays_and_a_removed_lot_goes()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var withdrawn = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var published = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        var removed = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(3));
        var at = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.FromHours(7));
        await api.UpdateAsync<HarvestLot>(withdrawn.Id(), l => (l.PublishedAt, l.WithdrawnAt, l.WithdrawalReason) = (at, at.AddDays(1), "Reason"));
        await api.UpdateAsync<HarvestLot>(published.Id(), l => (l.PublishedAt, l.WithdrawnAt) = (at.AddDays(2), at.AddDays(1)));
        await api.RemoveLotAsync(owner, removed.Id());
        await api.CloseCycleAsync(raft.CycleId);

        var list = await owner.LotsForSaleAsync();

        var lots = list.GetProperty("lots").EnumerateArray().ToDictionary(l => l.Id());
        Assert.Equal(2, lots.Count);
        Assert.Equal("Withdrawn", lots[withdrawn.Id()].Text("originState"));
        Assert.Equal(at.AddDays(1), lots[withdrawn.Id()].GetProperty("withdrawnAt").GetDateTimeOffset());
        Assert.DoesNotContain("Reason", list.GetRawText());
        Assert.Equal("Published", lots[published.Id()].Text("originState"));
        Assert.Equal(JsonNull, lots[published.Id()].GetProperty("withdrawnAt").ValueKind);
    }

    [Fact]
    public async Task Each_caller_reads_the_zones_they_can_access_and_the_filter_cannot_reach_further()
    {
        using var api = new AuthApiFactory();
        var raftA = await api.SeededRaftAsync();
        var owner = raftA.Owner.Client;
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var cycleB = (await owner.StartCycleAsync(raftB, new CycleBody(FarmingTestKit.Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleB, raftA.Lot.Session([1, 2]));
        var lotA = await owner.HarvestLotAsync(raftA.RaftId, HarvestFields.OfLanes(1));
        var lotB = await owner.HarvestLotAsync(raftB, HarvestFields.OfLanes(1));
        var (codeA, codeB) = (lotA.Text("lotCode"), lotB.Text("lotCode"));
        var managerA = await api.ManagerAsync(raftA.ZoneId);
        var allZones = await api.StaffAsync(null);

        Assert.Equal([codeA], await CodesAsync(managerA.Client));
        Assert.Equal([codeA], await CodesAsync(managerA.Client, raftA.ZoneId));
        Assert.Contains(codeA, await CodesAsync(owner));
        Assert.Contains(codeB, await CodesAsync(owner));
        Assert.Equal([codeB], await CodesAsync(owner, zoneB));
        Assert.Contains(codeB, await CodesAsync(allZones.Client));
        Assert.Equal([codeA], await CodesAsync(allZones.Client, raftA.ZoneId));

        Assert.Equal("MSG-GEN-08", await (await managerA.Client.GetAsync($"/api/harvest-lots/for-sale?zoneId={zoneB}")).ErrorCodeAsync());
    }

    [Fact]
    public async Task An_accountant_and_a_trader_are_refused_and_anonymous_callers_get_401()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var zone = await api.ZoneEntityAsync(raft.ZoneId);
        var anonymous = api.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = null;

        foreach (var client in new[] { (await api.StaffAsync(zone, StaffRole.Accountant)).Client, (await api.StaffAsync(null, StaffRole.Accountant)).Client, await api.TraderAsync() })
        {
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync("/api/harvest-lots/for-sale")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/api/harvest-lots/for-sale?zoneId={raft.ZoneId}")).ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/harvest-lots/for-sale")).StatusCode);
    }

    [Fact]
    public async Task An_empty_list_answers_200_with_msg_gen_10()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);

        var list = await manager.Client.LotsForSaleAsync(raft.ZoneId);

        Assert.Empty(list.ForSaleCodes());
        Assert.Equal("MSG-GEN-10", list.Text("messageCode"));
        Assert.Equal("There is no data for the zone you chose. Please choose again.", list.Text("message"));
    }

    // FARM-06 is Owner-only (§I.4.4), and BR-FARM-10 filters the open harvest lots by zone.
    [Fact]
    public async Task The_dashboard_count_holds_the_lots_open_for_sale_now_by_zone_for_the_owner_alone()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var emptyZone = (await owner.CreateZoneAsync()).Id();
        var before = await owner.ForSaleCountAsync();
        var open = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var reopened = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        var closed = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(3));
        var removed = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(4));
        await api.UpdateAsync<HarvestLot>(reopened.Id(), l => l.StatusId = StatusIds.HarvestLot.Reopened);
        await CloseAsync(api, closed.Id());
        await api.RemoveLotAsync(owner, removed.Id());

        Assert.Equal(before + 2, await owner.ForSaleCountAsync());
        Assert.Equal(2, await owner.ForSaleCountAsync(raft.ZoneId));
        Assert.Equal(0, await owner.ForSaleCountAsync(emptyZone));
        Assert.Equal(open.Id(), (await owner.LotsForSaleAsync(raft.ZoneId)).GetProperty("lots")[1].Id());

        var manager = await api.ManagerAsync(raft.ZoneId);
        var accountant = await api.StaffAsync(await api.ZoneEntityAsync(raft.ZoneId), StaffRole.Accountant);
        foreach (var client in new[] { manager.Client, accountant.Client, await api.TraderAsync() })
        {
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync("/api/harvest-lots/for-sale/count")).ErrorCodeAsync());
        }
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;

    private static async Task<List<string>> CodesAsync(HttpClient client, Guid? zoneId = null) => (await client.LotsForSaleAsync(zoneId)).ForSaleCodes();
}
