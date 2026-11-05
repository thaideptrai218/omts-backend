using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>BR-FARM-04 through the API: remove the last lanes, return the Removed ones in number order, then new numbers; never delete.</summary>
public class RaftLaneTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly int InUse = StatusIds.Lane.InUse;
    private static readonly int Removed = StatusIds.Lane.Removed;

    [Fact]
    public async Task Lanes_are_removed_from_the_end_restored_in_order_then_new_numbers_and_never_deleted()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewRaftBody(lanes: 5);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);
        var originalIds = Enumerable.Range(1, 5).Select(n => raft.LaneId(n)).ToList();

        var two = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 2, RowVersion = raft.Text("rowVersion") });
        Assert.Equal(2, two.GetProperty("laneCount").GetInt32());
        Assert.Equal([1, 2], two.LaneNumbers(InUse));
        Assert.Equal([3, 4, 5], two.LaneNumbers(Removed));
        Assert.Equal("Removed", two.GetProperty("lanes")[4].Text("status"));

        var three = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 3, RowVersion = two.Text("rowVersion") });
        Assert.Equal([1, 2, 3], three.LaneNumbers(InUse));
        Assert.Equal([4, 5], three.LaneNumbers(Removed));

        var seven = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 7, RowVersion = three.Text("rowVersion") });
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], seven.LaneNumbers(InUse));
        Assert.Empty(seven.LaneNumbers(Removed));

        // Numbers and ids are stable: the same rows, plus 6 and 7. No lane row was ever deleted.
        Assert.Equal(originalIds, Enumerable.Range(1, 5).Select(n => seven.LaneId(n)));
        Assert.Equal(7, await factory.QueryAsync(db => db.Lanes.CountAsync(l => l.RaftId == raft.Id())));

        var one = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 1, RowVersion = seven.Text("rowVersion") });
        Assert.Equal([2, 3, 4, 5, 6, 7], one.LaneNumbers(Removed));
        Assert.Equal(7, await factory.QueryAsync(db => db.Lanes.CountAsync(l => l.RaftId == raft.Id())));
    }

    [Fact]
    public async Task List_counts_lanes_in_use_and_the_view_of_today_shows_status_only()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var body = NewRaftBody(lanes: 3);
        var raft = await owner.CreateRaftAsync(zone.Id(), body);
        await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 2, RowVersion = raft.Text("rowVersion") });

        var row = (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}/rafts")).EnumerateArray().Single();
        Assert.Equal(2, row.GetProperty("laneCount").GetInt32());

        var today = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today");
        Assert.Equal((raft.Id(), raft.Text("code"), Today.ToString("yyyy-MM-dd")), (today.GetProperty("raftId").GetGuid(), today.Text("raftCode"), today.Text("date")));
        Assert.Equal(["In Use", "In Use", "Removed"], today.GetProperty("lanes").EnumerateArray().Select(l => l.Text("status")));
        Assert.All(today.GetProperty("lanes").EnumerateArray(), l => Assert.False(l.TryGetProperty("state", out _)));
    }

    // FARM-04.2.E1: nothing is saved, the cycle is named, and the same edit with the old number goes through.
    [Fact]
    public async Task Open_cycle_blocks_a_lane_change_and_discontinue_but_not_other_edits()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewRaftBody(lanes: 2);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);
        var cycle = await factory.AddCycleAsync(raft.Id(), DateTimeOffset.UtcNow.AddDays(-3));

        foreach (var count in new[] { 1, 3 })
        {
            var blocked = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = count, ConditionNotes = "Rope worn", RowVersion = raft.Text("rowVersion") }))
                .ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(("MSG-GEN-06", cycle.Id), (blocked.Text("code"), blocked.GetProperty("cycleId").GetGuid()));
            Assert.Contains("cultivation cycle opened", blocked.Text("detail"));
        }

        var kept = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.Equal([1, 2], kept.LaneNumbers(InUse));
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("conditionNotes").ValueKind);

        var notes = await owner.EditRaftAsync(raft.Id(), body with { ConditionNotes = "Rope worn", RowVersion = raft.Text("rowVersion") });
        Assert.Equal("Rope worn", notes.Text("conditionNotes"));

        var discontinue = await (await owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = notes.Text("rowVersion") })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-06", discontinue.Text("code"));
    }

    [Fact]
    public async Task A_closed_cycle_does_not_block_a_lane_change()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewRaftBody(lanes: 2);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);
        await factory.AddCycleAsync(raft.Id(), DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Equal([1], (await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 1, RowVersion = raft.Text("rowVersion") })).LaneNumbers(InUse));
    }

    // GBR-02: the values before and after, with the lanes that moved.
    [Fact]
    public async Task Each_lane_change_is_audited_with_before_and_after_and_a_same_number_edit_is_not()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewRaftBody(lanes: 3);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);

        var one = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 1, RowVersion = raft.Text("rowVersion") });
        var four = await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 4, RowVersion = one.Text("rowVersion") });
        await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 4, RowVersion = four.Text("rowVersion") });

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raft.Id() && e.Action == "Raft.LanesChanged").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, audits.Count);
        Assert.Equal(3, JsonNode.Parse(audits[0].Before!)!["LaneCount"]!.GetValue<int>());
        var removal = JsonNode.Parse(audits[0].After!)!;
        Assert.Equal(1, removal["LaneCount"]!.GetValue<int>());
        Assert.Equal([2, 3], removal["RemovedLanes"]!.AsArray().Select(n => n!.GetValue<int>()));
        var addition = JsonNode.Parse(audits[1].After!)!;
        Assert.Equal(1, JsonNode.Parse(audits[1].Before!)!["LaneCount"]!.GetValue<int>());
        Assert.Equal(4, addition["LaneCount"]!.GetValue<int>());
        Assert.Equal([2, 3], addition["RestoredLanes"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal([4], addition["NewLanes"]!.AsArray().Select(n => n!.GetValue<int>()));
    }
}
