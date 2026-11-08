using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Harvest.HarvestYieldTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-04 in §I.4.4: the Owner for any zone or all zones, a Zone Manager in the zones of their pairs (BR-FARM-08), the Accountant and traders not at all.</summary>
public class HarvestYieldAccessTests
{
    private sealed record TwoZones(Actor Owner, Guid ZoneA, Guid RaftA, Guid LotA, Guid ZoneB, Guid CycleB, Guid LotB);

    private static async Task<TwoZones> TwoZonesAsync(AuthApiFactory api)
    {
        var raftA = await api.SeededRaftAsync();
        var owner = raftA.Owner.Client;
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var cycleB = (await owner.StartCycleAsync(raftB, new CycleBody(FarmingTestKit.Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleB, raftA.Lot.Session([1, 2]));
        var lotA = await owner.HarvestLotAsync(raftA.RaftId);
        var lotB = await owner.HarvestLotAsync(raftB, new HarvestFields { Cages = "2" });
        return new TwoZones(raftA.Owner, raftA.ZoneId, raftA.RaftId, lotA.Id(), zoneB, cycleB, lotB.Id());
    }

    [Fact]
    public async Task A_zone_manager_reads_their_zone_and_gets_msg_gen_08_for_another()
    {
        using var api = new AuthApiFactory();
        var world = await TwoZonesAsync(api);
        var manager = (await api.ManagerAsync(world.ZoneA)).Client;

        Assert.Equal([world.LotA], (await manager.YieldAsync()).YieldLotIds());
        Assert.Equal([world.LotA], (await manager.YieldAsync(YieldFilters(groupBy: "zone", zoneIds: [world.ZoneA]))).YieldLotIds());

        foreach (var filters in new[] { YieldFilters(zoneIds: [world.ZoneB]), YieldFilters(zoneIds: [world.ZoneA, world.ZoneB]) })
        {
            Assert.Equal("MSG-GEN-08", await (await manager.GetYieldAsync(filters)).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await manager.GetYieldExportAsync(filters)).ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.OK, (await manager.GetYieldExportAsync(YieldFilters(zoneIds: [world.ZoneA]))).StatusCode);
    }

    [Fact]
    public async Task The_owner_reads_any_zone_or_all_zones_together_and_an_all_zones_manager_reads_every_zone()
    {
        using var api = new AuthApiFactory();
        var world = await TwoZonesAsync(api);
        var owner = world.Owner.Client;
        var allZones = (await api.StaffAsync(null)).Client;

        var all = await owner.YieldAsync(YieldFilters(groupBy: "zone"));
        Assert.Equal(new[] { world.ZoneA, world.ZoneB }.Order(), all.Groups().Select(g => g.GetProperty("key").GetGuid()).Order());
        Assert.Equal((500m, 2), (all.GetProperty("totals").Kg("estimatedKg"), all.GetProperty("totals").GetProperty("lotCount").GetInt32()));
        Assert.Equal([world.LotB], (await owner.YieldAsync(YieldFilters(zoneIds: [world.ZoneB]))).YieldLotIds());
        Assert.Equal(2, (await owner.YieldAsync(YieldFilters(zoneIds: [world.ZoneA, world.ZoneB, world.ZoneA]))).YieldLotIds().Count);
        Assert.Equal(2, (await allZones.YieldAsync()).YieldLotIds().Count);
        Assert.Equal([world.LotA], (await allZones.YieldAsync(YieldFilters(zoneIds: [world.ZoneA]))).YieldLotIds());
    }

    // §I.4.4 HARVEST-04: No for the Accountant (with a pair of one zone or of All zones) and for traders.
    [Fact]
    public async Task An_accountant_and_a_trader_are_refused_and_anonymous_callers_get_401()
    {
        using var api = new AuthApiFactory();
        var world = await TwoZonesAsync(api);
        var zone = await api.ZoneEntityAsync(world.ZoneA);
        var anonymous = api.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = null;

        foreach (var client in new[] { (await api.StaffAsync(zone, StaffRole.Accountant)).Client, (await api.StaffAsync(null, StaffRole.Accountant)).Client, await api.TraderAsync() })
        {
            Assert.Equal("MSG-GEN-08", await (await client.GetYieldAsync()).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.GetYieldAsync(YieldFilters(zoneIds: [world.ZoneA]))).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.GetYieldExportAsync()).ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetYieldAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetYieldExportAsync()).StatusCode);
    }

    // BR-CYCLE-04 locks cultivation records, not the report: the lots of a closed cycle still count, grouped under it.
    [Fact]
    public async Task A_lot_of_a_closed_cycle_still_counts()
    {
        using var api = new AuthApiFactory();
        var world = await TwoZonesAsync(api);
        var manager = (await api.ManagerAsync(world.ZoneB)).Client;
        await api.CloseCycleAsync(world.CycleB);

        var report = await manager.YieldAsync(YieldFilters(groupBy: "cycle"));

        var group = Assert.Single(report.Groups());
        Assert.Equal((world.CycleB, "Closed", 100m), (group.GetProperty("key").GetGuid(), group.Text("cycleStatus"), group.Kg("estimatedKg")));
    }
}
