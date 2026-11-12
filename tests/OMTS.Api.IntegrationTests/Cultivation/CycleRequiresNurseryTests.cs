using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The API as it runs everywhere but the tests of the old cycle screens: a cycle cannot be started by hand (owner decision of 12/11/2026, Q21).</summary>
public sealed class NurseryOnlyApiFactory : AuthApiFactory
{
    protected override bool ManualCycleStartAllowed => false;
}

/// <summary>
/// Owner decision of 12/11/2026 (Q21): a new cultivation cycle is created when a nursery batch is completed, not by hand. The old start (with a seed lot picked per session, the 110 % and 150 % plan tiers and a stock
/// issue) answers 400 <c>CYCLE_REQUIRES_NURSERY</c>; cycles already running (no nursery batch) stay readable, editable, harvestable, closable and take seeding sessions with a seed lot as before.
/// </summary>
public class CycleRequiresNurseryTests(NurseryOnlyApiFactory factory) : IClassFixture<NurseryOnlyApiFactory>
{
    [Fact]
    public async Task Starting_a_cycle_by_hand_answers_400_with_the_nursery_reason_and_saves_nothing()
    {
        var owner = await factory.OwnerAsync();
        var raftId = (await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id())).Id();
        var filesBefore = factory.StoredFiles("cycle-plan-photos").Count;

        var refused = await (await owner.Client.PostCycleAsync(raftId, new CycleBody(FarmingTestKit.Today.AddDays(-2)))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("CYCLE_REQUIRES_NURSERY", "Chu kỳ mới được tạo khi hoàn tất ươm."), (refused.Text("reasonCode"), refused.Text("hint")));
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.False(await factory.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raftId)));
        Assert.Equal(filesBefore, factory.StoredFiles("cycle-plan-photos").Count);
    }

    [Fact]
    public async Task A_cycle_is_still_created_by_completing_a_nursery_batch()
    {
        var world = await factory.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot);

        var done = await client.CompleteNurseryAsync(factory, batch.Id(), "900", new Share(world.Raft1, 900m));

        var cycle = await client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{done.GetProperty("allocations")[0].GetProperty("cycleId").GetGuid()}");
        Assert.Equal(batch.Id(), cycle.GetProperty("nurseryBatchId").GetGuid());
    }

    [Fact]
    public async Task A_cycle_already_running_stays_readable_editable_harvestable_and_takes_sessions_with_a_seed_lot_as_before()
    {
        var setup = await ManualCycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);

        var session = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1, 2]));
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");
        var edited = await owner.PutAsJsonAsync($"/api/cultivation-cycles/{setup.CycleId}", new { startDate = cycle.Text("startDate"), expectedSeedQuantity = 500m, rowVersion = cycle.Text("rowVersion") });
        var harvest = await owner.PostHarvestAsync(setup.RaftId, HarvestFields.OfLanes(1));
        var another = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([3]));

        Assert.Equal((2, HttpStatusCode.OK), (session.GetProperty("lanePercents").GetArrayLength(), edited.StatusCode));
        Assert.Equal(HttpStatusCode.Created, harvest.StatusCode);
        Assert.Equal(lot.LineId, another.GetProperty("seedLot").Id());
        Assert.True(cycle.IsNull("nurseryBatchId"));
    }

    // The old cycle is started the way the tests of the old screens do (the host allows it), then read through the host that does not.
    private async Task<(Actor Owner, Guid ZoneId, Guid RaftId, Guid CycleId)> ManualCycleAsync()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raftId = (await owner.Client.CreateRaftAsync(zoneId)).Id();
        var cycle = await factory.AddCycleAsync(raftId, FarmingTestKit.Today.AddDays(-10));
        return (owner, zoneId, raftId, cycle.Id);
    }
}
