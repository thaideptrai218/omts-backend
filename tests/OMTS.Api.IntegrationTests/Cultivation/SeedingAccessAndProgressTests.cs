using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>§I.4.4 CYCLE-02 (Owner Full, Zone Manager Full*, others No), BR-CYCLE-01 with seeding, and the views of CYCLE-05 that show the sessions.</summary>
public class SeedingAccessAndProgressTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Each_route_answers_each_actor_as_the_matrix_says()
    {
        var setup = await factory.CycleAsync();
        var lot = await setup.Owner.Client.SeedLotAsync();
        var record = await setup.Owner.Client.SeedingRecordAsync(setup.CycleId, lot.Session([1]));
        var zone = await factory.ZoneEntityAsync(setup.ZoneId);
        var inZone = await factory.StaffAsync(zone);
        var allZones = await factory.StaffAsync(null);
        var outside = await factory.ManagerAsync((await setup.Owner.Client.CreateZoneAsync()).Id());
        var accountant = await factory.StaffAsync(null, StaffRole.Accountant);
        var trader = await factory.TraderAsync();

        Task<HttpResponseMessage>[] Routes(HttpClient c) =>
        [
            c.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/seeding-records"),
            c.GetAsync($"/api/seeding-records/{record.Id()}"),
            c.GetAsync($"/api/rafts/{setup.RaftId}/seed-lots"),
            c.GetAsync($"/api/rafts/{setup.RaftId}/lanes/1/progress"),
            c.CompletionPreviewAsync(setup.CycleId),
        ];

        foreach (var client in new[] { inZone.Client, allZones.Client })
        {
            foreach (var answer in Routes(client))
            {
                await (await answer).ReadAsync(HttpStatusCode.OK);
            }
        }

        foreach (var client in new[] { outside.Client, accountant.Client, trader })
        {
            foreach (var answer in Routes(client))
            {
                var refused = await answer;
                Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (refused.StatusCode, await refused.ErrorCodeAsync()));
            }

            Assert.Equal("MSG-GEN-08", await (await client.PostSeedingAsync(setup.CycleId, lot.Session([2]))).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PutSeedingAsync(record.Id(), record.AsCorrection())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.RemoveSeedingAsync(record.Id())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostSeedingCompleteAsync(setup.CycleId)).ErrorCodeAsync());
        }

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/seeding-records")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostSeedingAsync(setup.CycleId, lot.Session())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/seeding-records/{record.Id()}/remove", new { reason = "x" })).StatusCode);
    }

    // BR-CYCLE-01: a cycle with a seeding session cannot be cancelled and its start date cannot pass the first session, as with a log.
    [Fact]
    public async Task A_cycle_with_seeding_cannot_be_cancelled_and_its_start_cannot_pass_the_first_session()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1], Today.AddDays(-6)));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([2], Today.AddDays(-4)));
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");

        Assert.False(cycle.Flag("canCancel"));
        Assert.Equal("MSG-GEN-06", await (await owner.CancelCycleAsync(setup.CycleId, cycle.Text("rowVersion"))).ErrorCodeAsync());

        var version = cycle.Text("rowVersion");
        Assert.Equal(["Start date"], await (await owner.PutCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-5), RowVersion: version))).InvalidFieldsAsync());
        var moved = await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-6), RowVersion: version));
        Assert.Equal(Today.AddDays(-6), moved.Date("startDate"));
    }

    [Fact]
    public async Task The_views_of_the_raft_show_growing_lanes_with_their_seed_lots_and_dates()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var (first, second) = (await owner.SeedLotAsync(), await owner.SeedLotAsync());
        await owner.RecordSeedingAsync(setup.CycleId, first.Session([1, 2], Today.AddDays(-5)));
        await owner.RecordSeedingAsync(setup.CycleId, second.Session([2], Today.AddDays(-3)));
        await owner.RecordSeedingAsync(setup.CycleId, first.Session([2], Today.AddDays(-2), 3m));

        var progress = await owner.ProgressAsync(setup.RaftId);

        Assert.Equal(new List<int> { 3, 4, 5 }, progress.Numbers("notSeededLanes"));
        var lane2 = progress.GetProperty("seedLots").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 2);
        var lots = lane2.GetProperty("seedLots").EnumerateArray().ToDictionary(l => l.GetProperty("seedLotId").GetGuid(), l => l.GetProperty("dates").EnumerateArray().Select(d => DateOnly.Parse(d.GetString()!)).ToList());
        Assert.Equal([Today.AddDays(-5), Today.AddDays(-2)], lots[first.LineId]);
        Assert.Equal([Today.AddDays(-3)], lots[second.LineId]);

        // On an earlier date only what was hung by then (BR-FARM-03).
        var earlier = await owner.ProgressAsync(setup.RaftId, $"?date={Today.AddDays(-4):yyyy-MM-dd}");
        Assert.Equal([1, 2], earlier.GetProperty("seedLots").EnumerateArray().Select(l => l.GetProperty("laneNumber").GetInt32()));
        var earlierLane2 = earlier.GetProperty("seedLots").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 2);
        Assert.Single(earlierLane2.GetProperty("seedLots").EnumerateArray());
        var state = earlier.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 1).Text("state");
        Assert.Equal("Growing", state);
        var before = await owner.ProgressAsync(setup.RaftId, $"?date={Today.AddDays(-6):yyyy-MM-dd}");
        Assert.All(before.GetProperty("lanes").EnumerateArray(), l => Assert.Equal("NotSeeded", l.Text("state")));
        Assert.Empty(before.GetProperty("seedLots").EnumerateArray());
    }

    // CYCLE-05.0 step 3 and CYCLE-05.2: a lane's sessions with lot, receipt and supplier, and the total per lot once the seeding is finished.
    [Fact]
    public async Task A_lane_shows_its_sessions_and_the_screen_the_total_per_seed_lot()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var record = await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1], Today.AddDays(-4), 7m));
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1, 2], Today.AddDays(-3), 5m));

        var lane = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}/lanes/1/progress");

        Assert.Equal(("Growing", setup.CycleId), (lane.Text("state"), lane.GetProperty("cycle").Id()));
        var sessions = lane.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal([Today.AddDays(-3), Today.AddDays(-4)], sessions.Select(s => s.Date("seedingDate")));
        Assert.Equal((record.Id(), lot.Code, lot.ReceiptId, lot.SupplierId, 7m), (sessions[1].Id(), sessions[1].GetProperty("seedLot").Text("code"), sessions[1].GetProperty("seedLot").GetProperty("receiptId").GetGuid(), sessions[1].GetProperty("seedLot").GetProperty("supplierId").GetGuid(), sessions[1].GetProperty("quantity").GetDecimal()));

        var total = Assert.Single((await owner.SeedingListAsync(setup.CycleId)).GetProperty("lots").EnumerateArray());
        Assert.Equal((lot.Code, 12m), (total.Text("code"), total.GetProperty("quantity").GetDecimal()));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{setup.RaftId}/lanes/9/progress")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{setup.RaftId}/lanes/1/progress?cycleId={Guid.NewGuid()}")).StatusCode);
        var noCycle = await owner.CreateRaftAsync(setup.ZoneId);
        var empty = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{noCycle.Id()}/lanes/1/progress");
        Assert.True(empty.IsNull("cycle"));
        Assert.Equal("NotSeeded", empty.Text("state"));
    }

    [Fact]
    public async Task The_screen_of_a_closed_cycle_keeps_its_sessions()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1]));
        await factory.CloseCycleAsync(setup.CycleId);

        var closed = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal(("Growing", 1), (closed.StateOf(1), closed.GetProperty("records").GetArrayLength()));

        // A lane Removed after the close stays on the screen of that cycle for its record, not counted (BR-FARM-04, BR-CYCLE-02); a Removed lane with no record leaves it.
        foreach (var number in new[] { 1, 5 })
        {
            var lane = await factory.QueryAsync(db => db.Lanes.SingleAsync(l => l.RaftId == setup.RaftId && l.LaneNumber == number));
            await factory.UpdateAsync<Lane>(lane.Id, l => l.StatusId = StatusIds.Lane.Removed);
        }

        var removed = await owner.SeedingListAsync(setup.CycleId);
        Assert.Equal(("Growing", 3, 0), (removed.StateOf(1), removed.GetProperty("laneCount").GetInt32(), removed.GetProperty("seededCount").GetInt32()));
        Assert.Equal([1, 2, 3, 4], removed.GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("laneNumber").GetInt32()));
    }
}
