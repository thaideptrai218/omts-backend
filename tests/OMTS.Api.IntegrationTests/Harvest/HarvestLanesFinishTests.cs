using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>Owner decision 12/11/2026: POST /api/rafts/{id}/finish-harvest finishes many lanes in one transaction, all or nothing.</summary>
public class HarvestLanesFinishTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Task<HttpResponseMessage> FinishAsync(HttpClient client, Guid raftId, params int[] lanes) =>
        client.PostAsJsonAsync($"/api/rafts/{raftId}/finish-harvest", new { laneNumbers = lanes });

    private Task<int> FinishedAuditCountAsync(Guid raftId) =>
        factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Count(a => a.Action == "HarvestLot.LaneFinished" && a.ZoneId != null)));

    [Fact]
    public async Task Finishes_several_lanes_once_with_an_audit_row_per_lane_and_a_summary_and_repeats_as_already_finished()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));
        var before = await FinishedAuditCountAsync(raft.RaftId);

        var done = await (await FinishAsync(owner, raft.RaftId, 1, 2)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal([1, 2], done.Numbers("finished"));
        Assert.Empty(done.Numbers("alreadyFinished"));
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal(["Harvested", "Harvested", "Growing"], [screen.HarvestStateOf(1), screen.HarvestStateOf(2), screen.HarvestStateOf(3)]);
        Assert.Equal(before + 2, await FinishedAuditCountAsync(raft.RaftId));
        Assert.Equal(1, await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Count(a => a.Action == "Raft.HarvestLanesFinished" && a.RecordId == raft.RaftId))));

        var again = await (await FinishAsync(owner, raft.RaftId, 2, 1)).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(again.Numbers("finished"));
        Assert.Equal([1, 2], again.Numbers("alreadyFinished"));
        Assert.Equal(before + 2, await FinishedAuditCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task One_lane_that_cannot_be_finished_refuses_the_whole_call_with_409_and_changes_nothing()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1)); // lane 2 has no lot, lane 99 does not exist

        var refused = await (await FinishAsync(owner, raft.RaftId, 1, 2, 99)).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        var lanes = refused.GetProperty("lanes").EnumerateArray().ToList();
        Assert.Equal([(2, "NotBeingHarvested"), (99, "LaneNotFound")], lanes.Select(l => (l.GetProperty("laneNumber").GetInt32(), l.Text("reasonCode"))));
        Assert.All(lanes, l => Assert.False(string.IsNullOrWhiteSpace(l.Text("message"))));
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal("BeingHarvested", screen.HarvestStateOf(1));
        Assert.Equal(0, await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Count(a => a.Action == "Raft.HarvestLanesFinished" && a.RecordId == raft.RaftId))));
    }

    [Fact]
    public async Task Finishing_the_last_seeded_lanes_says_the_cycle_can_be_closed()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));

        var done = await (await FinishAsync(owner, raft.RaftId, 1, 2)).ReadAsync(HttpStatusCode.OK);

        Assert.True(done.GetProperty("everySeededLaneHarvested").GetBoolean());
    }

    [Fact]
    public async Task A_stranger_gets_403_and_an_unknown_raft_404()
    {
        var raft = await factory.SeededRaftAsync();
        await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var otherZone = await (await factory.OwnerAsync()).Client.CreateZoneAsync();
        var stranger = await factory.StaffAsync(await factory.ZoneEntityAsync(otherZone.Id()));

        Assert.Equal(HttpStatusCode.Forbidden, (await FinishAsync(stranger.Client, raft.RaftId, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await FinishAsync(raft.Owner.Client, Guid.NewGuid(), 1)).StatusCode);
    }

    [Fact]
    public async Task A_list_of_more_than_200_lanes_empty_duplicated_or_not_positive_answers_400()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        foreach (var lanes in new[] { Enumerable.Range(1, 201).ToArray(), [], [1, 1], [0] })
        {
            var bad = await (await FinishAsync(owner, raft.RaftId, lanes)).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal("LaneNumbers", bad.Text("field"));
        }
    }
}
