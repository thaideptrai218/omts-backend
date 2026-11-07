using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// BR-CYCLE-02 through the API, the gap the C2 QA named now that lots feed the lane states: a session with a lane in a harvest lot is corrected only in TRACE-03, and lanes in a lot are not seeded again.
/// </summary>
public class SeedingBlockedByHarvestLotTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(SeededRaft Raft, Actor Manager, JsonElement Session)> SessionOfManagerAsync()
    {
        var raft = await factory.SeededRaftAsync(5);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var session = await manager.Client.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([1, 2]));
        return (raft, manager, session);
    }

    private static string InALotMessage(JsonElement session) =>
        $"The seeding session of raft {session.Text("raftCode")} on {session.Date("seedingDate"):dd/MM/yyyy} is in a harvest lot and cannot be used for this action.";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_lane_in_a_lot_blocks_the_correction_and_the_removal_of_its_session_also_for_the_owner(bool byOwner, bool finished)
    {
        var (raft, manager, session) = await SessionOfManagerAsync();
        var fields = HarvestFields.OfLanes(2);
        await raft.Owner.Client.HarvestLotAsync(raft.RaftId, finished ? fields.FinishedLanes(2) : fields);
        var client = byOwner ? raft.Owner.Client : manager.Client;

        var corrected = await (await client.PutSeedingAsync(session.Id(), session.AsCorrection() with { Quantity = 5m })).ReadAsync(HttpStatusCode.Conflict);
        var removed = await (await client.RemoveSeedingAsync(session.Id(), rowVersion: session.Text("rowVersion"))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-GEN-09", InALotMessage(session)), (corrected.Text("code"), corrected.Text("detail")));
        Assert.Equal(("MSG-GEN-09", InALotMessage(session)), (removed.Text("code"), removed.Text("detail")));
        Assert.Equal(1, await factory.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.Id == session.Id())));
        Assert.Equal(10m, (await client.GetSeedingAsync(session.Id())).GetProperty("quantity").GetDecimal());
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == session.Id() && e.Action != "SeedingRecord.Created")));
    }

    // Only a lane of the session counts: the session on other lanes stays correctable.
    [Fact]
    public async Task A_session_with_no_lane_in_a_lot_can_still_be_corrected_and_removed_and_says_which_can()
    {
        var (raft, manager, session) = await SessionOfManagerAsync();
        await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(5));

        var list = await manager.Client.SeedingListAsync(raft.CycleId);
        var ofLanes = list.GetProperty("records").EnumerateArray().ToDictionary(r => r.Id(), r => r.GetProperty("canCorrect").GetBoolean());
        Assert.True(ofLanes[session.Id()]);
        Assert.False(ofLanes[raft.Session.Id()]);

        (await manager.Client.PutSeedingAsync(session.Id(), session.AsCorrection() with { Quantity = 7m })).EnsureSuccessStatusCode();
        var now = await manager.Client.GetSeedingAsync(session.Id());
        (await manager.Client.RemoveSeedingAsync(session.Id(), rowVersion: now.Text("rowVersion"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_session_says_it_cannot_be_corrected_once_a_lane_is_in_a_lot()
    {
        var (raft, manager, session) = await SessionOfManagerAsync();
        Assert.True((await manager.Client.GetSeedingAsync(session.Id())).GetProperty("canCorrect").GetBoolean());

        await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        Assert.False((await manager.Client.GetSeedingAsync(session.Id())).GetProperty("canCorrect").GetBoolean());
        Assert.False((await raft.Owner.Client.GetSeedingAsync(session.Id())).GetProperty("canCorrect").GetBoolean());
    }

    // BR-CYCLE-02: "Lanes that are Being harvested or Harvested are not seeded again in the cycle."
    [Fact]
    public async Task A_lane_in_a_lot_cannot_be_seeded_again_before_or_after_the_mark_and_a_correction_cannot_add_it()
    {
        var raft = await factory.SeededRaftAsync(1, 2, 3);
        var owner = raft.Owner.Client;
        var free = await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([4]));
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(2));

        var list = await owner.SeedingListAsync(raft.CycleId);
        Assert.Equal(("BeingHarvested", "Harvested", "Growing"), (list.StateOf(1), list.StateOf(2), list.StateOf(3)));

        var refused = await (await owner.PostSeedingAsync(raft.CycleId, raft.Lot.Session([1, 2, 3]))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Lanes", "Lanes 1, 2 cannot be seeded now."), (refused.Text("field"), refused.Text("hint")));

        var added = await (await owner.PutSeedingAsync(free.Id(), free.AsCorrection() with { LaneNumbers = [4, 1] })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Lanes", added.Text("field"));

        Assert.Equal(raft.Lot.LineId, (await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([3]))).GetProperty("seedLot").Id());
    }

    // BR-FARM-03: Growing lanes and Not seeded lanes count as before; a lane in a lot still has seed.
    [Fact]
    public async Task The_seeded_figures_and_the_offer_to_mark_complete_count_a_lane_in_a_lot_as_seeded()
    {
        var raft = await factory.SeededRaftAsync(1, 2, 3, 4);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        var saved = await owner.RecordSeedingAsync(raft.CycleId, raft.Lot.Session([5]));

        Assert.True(saved.GetProperty("offerMarkComplete").GetBoolean());
        var preview = await (await owner.CompletionPreviewAsync(raft.CycleId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(5, preview.GetProperty("seededCount").GetInt32());
    }

    // The close day still belongs to the closed cycle, and a lot of an old cycle leaves the next one's lanes free.
    [Fact]
    public async Task The_lots_of_a_closed_cycle_do_not_reach_the_next_cycle_and_the_lane_view_shows_only_the_cycle_shown()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        await factory.UpdateAsync<CultivationCycle>(raft.CycleId, c =>
        {
            c.StatusId = OMTS.Domain.Constants.StatusIds.CultivationCycle.Closed;
            c.CloseDate = Today.AddDays(-1);
        });

        var next = await owner.StartCycleAsync(raft.RaftId);

        var progress = await owner.ProgressAsync(raft.RaftId);
        Assert.Equal("NotSeeded", progress.GetProperty("lanes")[0].Text("state"));
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/1/progress?cycleId={next.Id()}")).GetProperty("harvestLots").EnumerateArray());
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/1/progress?cycleId={raft.CycleId}")).GetProperty("harvestLots").EnumerateArray());
        Assert.Empty((await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("lots").EnumerateArray());
    }
}
