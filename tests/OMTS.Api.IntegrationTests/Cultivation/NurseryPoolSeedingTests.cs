using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// CYCLE-02 from a nursery pool (v2, 13/11/2026): a session of a cycle that draws from a batch takes any quantity, capped by what the pool of the batch has left over every cycle; the lot is the batch's (the client
/// sends none), the stock ledger is not touched, the % of the lanes is independent of the quantity; a correction or a removal gives back or takes the difference and is blocked after the seeding is marked complete.
/// </summary>
public sealed class NurseryPoolSeedingTests
{
    // 10 000 in, 9 500 survive. 3 000 on 40 % of lanes 1 and 2: the quantity and the % are two figures, nothing links them.
    [Fact]
    public async Task A_session_takes_the_quantity_it_names_from_the_pool_with_the_lot_of_the_batch_and_touches_no_stock()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var cycle = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());
        var issuesBefore = await api.QueryAsync(db => db.InputIssues.CountAsync());

        var record = await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([1, 2], 3000m).Percents(40m, 1, 2));

        Assert.Equal((world.Lot.LineId, 3000m), (record.GetProperty("seedLot").Id(), record.Pieces("quantity")));
        Assert.Equal([1, 2], record.Lanes());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("issueId").ValueKind);
        Assert.Equal(issuesBefore, await api.QueryAsync(db => db.InputIssues.CountAsync()));
        Assert.Equal(batch.Id(), await api.QueryAsync(db => db.SeedingRecords.Where(r => r.Id == record.Id()).Select(r => r.NurseryBatchId).SingleAsync()));
        // The lot was emptied when the nursery started (10 000): hanging seed from it must not count again, which would show -3 000.
        Assert.Equal(0m, (await client.GetNurseryAsync(batch.Id())).Pieces("seedLotOnHand"));
        var view = await client.CycleViewAsync(cycle.Id());
        Assert.Equal((3000m, 6500m), (view.Pieces("nurserySeeded"), view.Pieces("nurseryRemaining")));
        var detail = await client.GetNurseryAsync(batch.Id());
        Assert.Equal((3000m, 6500m), (detail.Pieces("seeded"), detail.Pieces("remaining")));
        var offered = Assert.Single((await client.AvailableNurseriesAsync(world.ZoneId)).GetProperty("batches").EnumerateArray());
        Assert.Equal((3000m, 6500m), (offered.Pieces("seeded"), offered.Pieces("remaining")));

        // A lot named by the client is ignored: the lot is the batch's. Photos are still required.
        var other = await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([3], 100m) with { SeedLotId = Guid.NewGuid() });
        Assert.Equal(world.Lot.LineId, other.GetProperty("seedLot").Id());
        Assert.Equal("Photos", (await (await client.PostSeedingAsync(cycle.Id(), NurseryTestKit.NurserySession([4], 100m), photos: 0)).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        Assert.Equal(["Quantity"], await (await client.PostSeedingAsync(cycle.Id(), NurseryTestKit.NurserySession([4], 0m))).InvalidFieldsAsync());
        Assert.Equal(["Quantity"], await (await client.PostSeedingAsync(cycle.Id(), new SeedingBody(Today, null, [4], null))).InvalidFieldsAsync());
    }

    [Fact]
    public async Task The_pool_is_shared_by_every_cycle_of_the_batch_and_a_session_above_what_is_left_is_refused()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var first = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());
        var second = await client.StartNurseryCycleAsync(world.Raft2, batch.Id());
        await client.SeedingRecordAsync(first.Id(), NurseryTestKit.NurserySession([1], 3000m));
        await client.SeedingRecordAsync(second.Id(), NurseryTestKit.NurserySession([1], 4000m));

        var over = await (await client.PostSeedingAsync(second.Id(), NurseryTestKit.NurserySession([2], 2500.01m))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("NURSERY_POOL_EXCEEDED", "MSG-GEN-09", "Quantity", 2500m, 2500.01m), (over.Text("reasonCode"), over.Text("code"), over.Text("field"), over.Pieces("remaining"), over.Pieces("quantity")));
        Assert.Contains("còn 2500 mảnh", over.Text("detail"));
        Assert.Equal(2, await api.QueryAsync(db => db.SeedingRecords.CountAsync()));

        // Exactly what is left is fine, from either cycle; then the pool is empty: the batch is no longer offered and one piece more is refused.
        await client.SeedingRecordAsync(first.Id(), NurseryTestKit.NurserySession([2], 2500m));
        Assert.Empty((await client.AvailableNurseriesAsync(world.ZoneId)).GetProperty("batches").EnumerateArray());
        var empty = await (await client.PostSeedingAsync(second.Id(), NurseryTestKit.NurserySession([2], 1m))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_POOL_EXCEEDED", 0m), (empty.Text("reasonCode"), empty.Pieces("remaining")));
        Assert.Equal((0m, 5500m, 4000m), ((await client.CycleViewAsync(second.Id())).Pieces("nurseryRemaining"), (await client.CycleViewAsync(first.Id())).Pieces("nurserySeeded"), (await client.CycleViewAsync(second.Id())).Pieces("nurserySeeded")));
    }

    // 3 000 seeded of 9 500: raising the record to 5 000 takes 2 000 more, lowering to 1 000 gives 4 000 back, a quantity above the pool is refused, and removing the record gives it all back.
    [Fact]
    public async Task A_correction_or_a_removal_takes_or_gives_back_the_difference_and_never_passes_the_pool()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var first = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());
        var second = await client.StartNurseryCycleAsync(world.Raft2, batch.Id());
        var record = await client.SeedingRecordAsync(first.Id(), NurseryTestKit.NurserySession([1, 2], 3000m));
        await client.SeedingRecordAsync(second.Id(), NurseryTestKit.NurserySession([1], 1000m));
        async Task<decimal> RemainingAsync() => (await client.GetNurseryAsync(batch.Id())).Pieces("remaining");
        async Task<HttpResponseMessage> CorrectAsync(decimal quantity) =>
            await client.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = quantity, RowVersion = await api.IssueRowVersionAsync<SeedingRecord>(record.Id()) });
        Assert.Equal(5500m, await RemainingAsync());

        Assert.Equal(5000m, (await (await CorrectAsync(5000m)).ReadAsync(HttpStatusCode.OK)).GetProperty("record").Pieces("quantity"));
        Assert.Equal(3500m, await RemainingAsync());
        var tooMany = await (await CorrectAsync(8501m)).ReadAsync(HttpStatusCode.Conflict); // 9 500 less the 1 000 of the other cycle is 8 500
        Assert.Equal(("NURSERY_POOL_EXCEEDED", 8500m, 8501m), (tooMany.Text("reasonCode"), tooMany.Pieces("remaining"), tooMany.Pieces("quantity")));
        Assert.Equal(3500m, await RemainingAsync());
        Assert.Equal(8500m, (await (await CorrectAsync(8500m)).ReadAsync(HttpStatusCode.OK)).GetProperty("record").Pieces("quantity"));
        Assert.Equal(0m, await RemainingAsync());
        Assert.Equal(1000m, (await (await CorrectAsync(1000m)).ReadAsync(HttpStatusCode.OK)).GetProperty("record").Pieces("quantity"));
        Assert.Equal(7500m, await RemainingAsync());
        Assert.Equal(["Quantity"], await (await client.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = null, RowVersion = await api.IssueRowVersionAsync<SeedingRecord>(record.Id()) })).InvalidFieldsAsync());

        await (await client.RemoveSeedingAsync(record.Id(), "Nhập nhầm", await api.IssueRowVersionAsync<SeedingRecord>(record.Id()))).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal((8500m, 1000m, 0m), (await RemainingAsync(), (await client.GetNurseryAsync(batch.Id())).Pieces("seeded"), (await client.CycleViewAsync(first.Id())).Pieces("nurserySeeded")));
        var usage = Assert.Single((await client.GetNurseryAsync(batch.Id())).GetProperty("usage").EnumerateArray());
        Assert.Equal(world.Raft2, usage.GetProperty("raftId").GetGuid());
        Assert.Equal(0, await api.QueryAsync(db => db.InputIssues.CountAsync(i => i.SourceKind == OMTS.Domain.Enums.InputIssueSource.Seeding)));
    }

    [Fact]
    public async Task A_session_can_no_longer_be_added_corrected_or_removed_once_the_seeding_is_marked_complete()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var cycle = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());
        var record = await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([1, 2, 3, 4, 5], 2000m));
        await client.MarkSeedingCompleteAsync(cycle.Id());

        var add = await (await client.PostSeedingAsync(cycle.Id(), NurseryTestKit.NurserySession([1], 10m))).ReadAsync(HttpStatusCode.Conflict);
        var put = await (await client.PutSeedingAsync(record.Id(), record.AsCorrection() with { Quantity = 1000m, RowVersion = await api.IssueRowVersionAsync<SeedingRecord>(record.Id()) })).ReadAsync(HttpStatusCode.Conflict);
        var remove = await (await client.RemoveSeedingAsync(record.Id(), "Muộn", await api.IssueRowVersionAsync<SeedingRecord>(record.Id()))).ReadAsync(HttpStatusCode.Conflict);

        Assert.All(new[] { add, put, remove }, p => Assert.Equal(("SEEDING_COMPLETED", "MSG-GEN-09"), (p.Text("reasonCode"), p.Text("code"))));
        Assert.Equal(2000m, (await client.GetNurseryAsync(batch.Id())).Pieces("seeded"));
        Assert.False((await client.SeedingListAsync(cycle.Id())).Flag("canRecord"));
    }

    // A lane at 100 % is hidden from the choice of the next session (still on the overview); a lane at 60 % stays with 40 % left.
    [Fact]
    public async Task A_lane_at_100_percent_is_not_offered_again_and_a_lane_part_hung_shows_what_is_left()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var cycle = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());

        await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([1, 2], 3000m));
        await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([3], 4000m).Percents(60m, 3));
        var list = await client.SeedingListAsync(cycle.Id());

        Assert.Equal([3, 4, 5], list.Numbers("seedableLaneNumbers"));
        var lane3 = list.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 3);
        Assert.Equal((60m, 40m), (lane3.Pieces("seededPercent"), lane3.Pieces("remainingPercent")));
        var lane1 = list.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 1);
        Assert.Equal((100m, 0m), (lane1.Pieces("seededPercent"), lane1.Pieces("remainingPercent")));
        Assert.Equal(5, list.GetProperty("lanes").GetArrayLength()); // every lane stays on the overview
        Assert.Equal((2500m, 7000m), (list.Pieces("nurseryRemaining"), list.Pieces("nurserySeeded")));
        // Lane 1 is at 100 %: another session on it is refused on the percent, not on the pool.
        var again = await (await client.PostSeedingAsync(cycle.Id(), NurseryTestKit.NurserySession([1], 10m))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("LanePercent", again.Text("field"));
    }

    // Two cycles drew 3 000 and 4 000 from a batch of 10 000 in, 9 500 survived; closing wrote the 2 500 left off. The loss of the batch (500 + 2 500) is shared by what each cycle drew (3/7 and 4/7).
    [Fact]
    public async Task The_summary_of_a_cycle_shares_the_loss_of_the_batch_by_what_each_cycle_seeded()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var first = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());
        var second = await client.StartNurseryCycleAsync(world.Raft2, batch.Id());
        await client.SeedingRecordAsync(first.Id(), NurseryTestKit.NurserySession([1], 3000m));
        await client.SeedingRecordAsync(second.Id(), NurseryTestKit.NurserySession([1], 4000m));
        await (await client.PostCloseAsync(api, batch.Id())).ReadAsync(HttpStatusCode.OK);

        var one = (await client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{first.Id()}/summary")).GetProperty("nursery");
        var two = (await client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{second.Id()}/summary")).GetProperty("nursery");

        Assert.Equal((batch.Text("code"), 4285.714m, 4071.429m, 1285.714m, 30.0m, true), (one.Text("batchCode"), one.Pieces("quantityIn"), one.Pieces("survivors"), one.Pieces("lossPieces"), one.Pieces("lossPercent"), one.Flag("estimated")));
        Assert.Equal((5714.286m, 30.0m), (two.Pieces("quantityIn"), two.Pieces("lossPercent")));
        Assert.Equal(3000m, Math.Round(one.Pieces("lossPieces") + two.Pieces("lossPieces"), 0));
    }
}
