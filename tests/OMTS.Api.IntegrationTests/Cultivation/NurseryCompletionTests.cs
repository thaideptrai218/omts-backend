using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// NURSERY-03 (ươm giống, decision of 12/11/2026): completing a nursery records the survivors, takes 1 to 3 photos and, in one save, creates a cycle for each raft chosen with its share of the survivors
/// (adding up to them exactly), with no expected harvest date. A refusal saves nothing. Each test starts its own host, as it counts files.
/// </summary>
public sealed class NurseryCompletionTests
{
    // 10 000 pieces in, 9 500 survive and go to two rafts: the cycles carry the shares, the plan and the nursery, and the loss is an estimate.
    [Fact]
    public async Task Completing_a_nursery_for_two_rafts_creates_two_cycles_with_their_shares_and_no_expected_harvest_date()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "10000");

        var form = await client.PostCompleteAsync(api, batch.Id(), "9500", [new Share(world.Raft1, 4750m, 600m), new Share(world.Raft2, 4750m)], [Jpeg, Png]);
        var done = (await form.ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((StatusIds.NurseryBatch.Completed, "Đã hoàn tất", Today, 9500m, 500m, 5.0m, true),
            (done.GetProperty("statusId").GetInt32(), done.Text("statusLabel"), done.Date("completedOn"), done.Pieces("survivors"), done.Pieces("lossPieces"), done.Pieces("lossPercent"), done.Flag("estimated")));
        Assert.Equal(2, done.GetProperty("completionPhotos").GetArrayLength());
        Assert.Equal(1, done.GetProperty("startPhotos").GetArrayLength());
        Assert.True(done.Flag("canCorrectSurvivors") && !done.Flag("canEdit") && !done.Flag("canComplete"));
        Assert.Equal(2, api.StoredFiles(FileCategories.NurseryCompletionPhotos).Count);

        var cycles = await api.NurseryCyclesAsync(batch.Id());
        Assert.Equal(2, cycles.Count);
        Assert.All(cycles, c => Assert.Equal((Today, StatusIds.CultivationCycle.Open, 4750m, 4750m, (DateOnly?)null), (c.StartDate, c.StatusId, c.ExpectedSeedQuantity, c.NurseryAllocatedQuantity, c.ExpectedHarvestDate)));
        Assert.Equal(600m, cycles.Single(c => c.RaftId == world.Raft1).TargetYieldKg);
        Assert.Null(cycles.Single(c => c.RaftId == world.Raft2).TargetYieldKg);

        // The cycle answer, the progress and the seeding screen carry the nursery; the summary shows the loss of the cycle's part (estimates).
        var allocation = done.AllocationOf(world.Raft1);
        var cycle = await client.CycleViewAsync(allocation.GetProperty("cycleId").GetGuid());
        Assert.Equal((batch.Id(), batch.Text("code"), 4750m, 0m, 4750m), (cycle.GetProperty("nurseryBatchId").GetGuid(), cycle.Text("nurseryCode"), cycle.Pieces("nurseryAllocated"), cycle.Pieces("nurserySeeded"), cycle.Pieces("nurseryRemaining")));
        Assert.False(cycle.TryGetProperty("expectedHarvestDate", out _));
        var list = await client.SeedingListAsync(cycle.Id());
        Assert.Equal((batch.Text("code"), 4750m, 4750m), (list.Text("nurseryCode"), list.Pieces("nurseryAllocated"), list.Pieces("nurseryRemaining")));
        var progress = (await client.ProgressAsync(world.Raft1)).GetProperty("cycle");
        Assert.Equal(batch.Text("code"), progress.Text("nurseryCode"));
        var summary = (await client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycle.Id()}/summary")).GetProperty("nursery");
        Assert.Equal((5000m, 4750m, 250m, 5.0m, true, batch.Text("code")),
            (summary.Pieces("quantityIn"), summary.Pieces("survivors"), summary.Pieces("lossPieces"), summary.Pieces("lossPercent"), summary.Flag("estimated"), summary.Text("batchCode")));

        // One audit row for the completion and one for each cycle started, and the history shows the survivors from nothing to 9 500.
        Assert.Equal(2, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "CultivationCycle.Started" && e.ZoneId == world.ZoneId)));
        var history = await client.NurseryHistoryAsync(batch.Id());
        Assert.Equal(["Created", "Completed"], history.GetProperty("entries").EnumerateArray().Select(e => e.Text("action")).ToList());
        var completion = history.GetProperty("entries")[1];
        Assert.Equal("Hoàn tất ươm", completion.Text("actionLabel"));
        var change = completion.GetProperty("changes").EnumerateArray().Single(c => c.Text("field") == "Survivors");
        Assert.Equal(("Số mảnh sống sót (ước tính)", "9500"), (change.Text("label"), change.Text("to")));
        Assert.Equal(JsonValueKind.Null, change.GetProperty("from").ValueKind);

        // The batch is no longer for the seeding screen of other batches' sake: it is listed with the part of each raft left to hang.
        var available = (await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches/available?zoneId={world.ZoneId}")).GetProperty("batches");
        var offered = Assert.Single(available.EnumerateArray());
        Assert.Equal((9500m, 2), (offered.Pieces("remaining"), offered.GetProperty("allocations").GetArrayLength()));
    }

    [Fact]
    public async Task Completing_a_nursery_for_one_raft_gives_it_all_the_survivors()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var batch = await world.Owner.Client.StartNurseryAsync(world.ZoneId, world.Lot, "10000");

        var done = await world.Owner.Client.CompleteNurseryAsync(api, batch.Id(), "9500", new Share(world.Raft1, 9500m));

        var allocation = Assert.Single(done.GetProperty("allocations").EnumerateArray());
        Assert.Equal((world.Raft1, 9500m, 0m, 9500m, true), (allocation.GetProperty("raftId").GetGuid(), allocation.Pieces("quantity"), allocation.Pieces("seeded"), allocation.Pieces("remaining"), allocation.Flag("canChange")));
        Assert.Single(await api.NurseryCyclesAsync(batch.Id()));
    }

    [Fact]
    public async Task A_nursery_where_nothing_survived_completes_with_no_cycle()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var batch = await world.Owner.Client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");

        var done = (await (await world.Owner.Client.PostCompleteAsync(api, batch.Id(), "0", [])).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((0m, 1000m, 100.0m), (done.Pieces("survivors"), done.Pieces("lossPieces"), done.Pieces("lossPercent")));
        Assert.Empty(done.GetProperty("allocations").EnumerateArray());
        Assert.Empty(await api.NurseryCyclesAsync(batch.Id()));
    }

    [Fact]
    public async Task A_completion_that_is_refused_saves_no_cycle_no_photo_and_keeps_the_batch_nursing()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "10000");
        var otherZone = (await client.CreateZoneAsync()).Id();
        var strangerRaft = (await client.CreateRaftAsync(otherZone)).Id();
        await client.StartCycleAsync(world.Raft2);

        // MSG-GEN-01 on the field: the total, the survivors, the photos, a raft given twice, a date before the start.
        Assert.Equal(["Allocations"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", [new Share(world.Raft1, 4750m), new Share(world.Raft2, 4749m)])).InvalidFieldsAsync());
        Assert.Equal(["Allocations"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", [new Share(world.Raft1, 4750m), new Share(world.Raft1, 4750m)])).InvalidFieldsAsync());
        Assert.Equal(["Allocations"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", [])).InvalidFieldsAsync());
        Assert.Equal(["Survivors"], await (await client.PostCompleteAsync(api, batch.Id(), "10001", [new Share(world.Raft1, 10001m)])).InvalidFieldsAsync());
        Assert.Equal(["Survivors"], await (await client.PostCompleteAsync(api, batch.Id(), null, [new Share(world.Raft1, 1m)])).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", [new Share(world.Raft1, 9500m)], [])).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", [new Share(world.Raft1, 9500m)], [Jpeg, Jpeg, Jpeg, Jpeg])).InvalidFieldsAsync());
        Assert.Equal(["Completed on"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", [new Share(world.Raft1, 9500m)], null, Today.AddDays(-30).ToString("yyyy-MM-dd"))).InvalidFieldsAsync());

        // 409 with a reason code: a raft with an Open cycle, a raft of another zone.
        var open = await (await client.PostCompleteAsync(api, batch.Id(), "9500", [new Share(world.Raft1, 4750m), new Share(world.Raft2, 4750m)])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_RAFT_HAS_OPEN_CYCLE", "MSG-GEN-09", world.Raft2), (open.Text("reasonCode"), open.Text("code"), open.GetProperty("raftId").GetGuid()));
        var stranger = await (await client.PostCompleteAsync(api, batch.Id(), "9500", [new Share(strangerRaft, 9500m)])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_RAFT_OTHER_ZONE", stranger.Text("reasonCode"));

        var current = await client.GetNurseryAsync(batch.Id());
        Assert.Equal((StatusIds.NurseryBatch.Nursing, 0), (current.GetProperty("statusId").GetInt32(), current.GetProperty("completionPhotos").GetArrayLength()));
        Assert.Empty(await api.NurseryCyclesAsync(batch.Id()));
        Assert.Empty(api.StoredFiles(FileCategories.NurseryCompletionPhotos));
        Assert.Equal(0, await api.QueryAsync(db => db.NurseryBatchAllocations.CountAsync()));
    }

    // A raft whose previous cycle closed today cannot start a new one the same day (BR-CYCLE-01): the completion names it.
    [Fact]
    public async Task A_raft_closed_the_day_of_the_completion_is_refused_with_its_close_date()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var batch = await world.Owner.Client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");
        await api.AddCycleAsync(world.Raft1, Today.AddDays(-20), Today);

        var refused = await (await world.Owner.Client.PostCompleteAsync(api, batch.Id(), "900", [new Share(world.Raft1, 900m)])).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("NURSERY_CYCLE_STARTS_BEFORE_CLOSE", Today.ToString("yyyy-MM-dd")), (refused.Text("reasonCode"), refused.Text("previousCloseDate")));
    }

    [Fact]
    public async Task A_zone_manager_completes_and_the_owner_is_told_a_second_completion_is_refused()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var manager = await api.ManagerAsync(world.ZoneId);
        var batch = await manager.Client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");

        await manager.Client.CompleteNurseryAsync(api, batch.Id(), "900", new Share(world.Raft1, 900m));

        Assert.Equal(2, (await api.NurseryNoticesAsync(batch.Id())).Count(n => n.RecipientAccountId == world.Owner.Account.Id)); // started and completed
        var again = await (await manager.Client.PostCompleteAsync(api, batch.Id(), "900", [new Share(world.Raft2, 900m)])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_NOT_NURSING", again.Text("reasonCode"));
    }

    // Owner decision of 12/11/2026: seeding from a nursery is capped by the share of the cycle, needs no seed lot, posts no issue and counts no seed twice.
    [Fact]
    public async Task Seeding_from_a_nursery_cycle_needs_no_lot_is_capped_by_its_share_and_posts_no_issue()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "10000");
        var done = await client.CompleteNurseryAsync(api, batch.Id(), "9500", new Share(world.Raft1, 4750m), new Share(world.Raft2, 4750m));
        var cycleId = done.AllocationOf(world.Raft1).GetProperty("cycleId").GetGuid();
        var issuesBefore = await api.QueryAsync(db => db.InputIssues.CountAsync());

        var record = await client.SeedingRecordAsync(cycleId, NurseryTestKit.NurserySession([1, 2], 4000m));

        Assert.Equal((world.Lot.LineId, 4000m), (record.GetProperty("seedLot").Id(), record.Pieces("quantity")));
        Assert.Equal(JsonValueKind.Null, record.GetProperty("issueId").ValueKind);
        Assert.Equal(issuesBefore, await api.QueryAsync(db => db.InputIssues.CountAsync()));
        Assert.Equal(batch.Id(), await api.QueryAsync(db => db.SeedingRecords.Where(r => r.Id == record.Id()).Select(r => r.NurseryBatchId).SingleAsync()));
        // The lot was emptied when the nursery started (10 000): hanging seed from it must not count again, which would show -4 000.
        Assert.Equal(0m, (await client.GetNurseryAsync(batch.Id())).Pieces("seedLotOnHand"));

        var over = await (await client.PostSeedingAsync(cycleId, NurseryTestKit.NurserySession([3], 751m))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_SEEDING_ABOVE_ALLOCATION", 4750m, 4000m, 750m, 751m), (over.Text("reasonCode"), over.Pieces("allocated"), over.Pieces("seeded"), over.Pieces("remaining"), over.Pieces("quantity")));
        Assert.Equal(1, await api.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == cycleId)));

        // Exactly what is left is fine, with no question about 110 % of a plan.
        var rest = await client.RecordSeedingAsync(cycleId, NurseryTestKit.NurserySession([3], 750m));
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("record").GetProperty("issueId").ValueKind);
        var view = await client.CycleViewAsync(cycleId);
        Assert.Equal((4750m, 0m), (view.Pieces("nurserySeeded"), view.Pieces("nurseryRemaining")));
        var left = (await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches/available?zoneId={world.ZoneId}")).GetProperty("batches")[0];
        Assert.Equal((4750m, world.Raft2), (left.Pieces("remaining"), left.GetProperty("allocations")[0].GetProperty("raftId").GetGuid()));

        // A correction that would pass the share is refused too; one within it is saved and keeps the batch's lot.
        var correction = record.AsCorrection() with { Quantity = 4001m, SeedLotId = Guid.NewGuid(), RowVersion = await api.IssueRowVersionAsync<SeedingRecord>(record.Id()) };
        var refused = await (await client.PutSeedingAsync(record.Id(), correction)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_SEEDING_ABOVE_ALLOCATION", refused.Text("reasonCode"));
        var fine = (await (await client.PutSeedingAsync(record.Id(), correction with { Quantity = 3500m })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.Equal((3500m, world.Lot.LineId), (fine.Pieces("quantity"), fine.GetProperty("seedLot").Id()));
        Assert.Equal(issuesBefore, await api.QueryAsync(db => db.InputIssues.CountAsync()));
    }

    [Fact]
    public async Task Correcting_the_survivors_of_one_raft_moves_its_share_and_is_kept_old_to_new_with_the_reason()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "10000");
        var done = await client.CompleteNurseryAsync(api, batch.Id(), "9500", new Share(world.Raft1, 9500m));
        var cycleId = done.AllocationOf(world.Raft1).GetProperty("cycleId").GetGuid();
        await client.SeedingRecordAsync(cycleId, NurseryTestKit.NurserySession([1], 5000m));

        // Not below what was hung, not above what went in, and a reason is needed.
        var below = await (await client.PostSurvivorsAsync(api, batch.Id(), 4999m, "Đếm lại")).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_SURVIVORS_BELOW_SEEDED", 5000m), (below.Text("reasonCode"), below.Pieces("seeded")));
        Assert.Equal(["Survivors"], await (await client.PostSurvivorsAsync(api, batch.Id(), 10001m, "Đếm lại")).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await client.PostSurvivorsAsync(api, batch.Id(), 9200m, " ")).InvalidFieldsAsync());

        var corrected = (await (await client.PostSurvivorsAsync(api, batch.Id(), 9200m, "Đếm lại")).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((9200m, 800m, 9200m), (corrected.Pieces("survivors"), corrected.Pieces("lossPieces"), corrected.AllocationOf(world.Raft1).Pieces("quantity")));
        var cycle = await api.QueryAsync(db => db.CultivationCycles.SingleAsync(c => c.Id == cycleId));
        Assert.Equal((9200m, 9200m), (cycle.NurseryAllocatedQuantity, cycle.ExpectedSeedQuantity));
        var entry = (await client.NurseryHistoryAsync(batch.Id())).GetProperty("entries").EnumerateArray().Single(e => e.Text("action") == "SurvivorsCorrected");
        Assert.Equal(("Sửa số sống sót", "Đếm lại"), (entry.Text("actionLabel"), entry.Text("reason")));
        var survivors = entry.GetProperty("changes").EnumerateArray().Single(c => c.Text("field") == "Survivors");
        Assert.Equal(("9500", "9200"), (survivors.Text("from"), survivors.Text("to")));
    }

    [Fact]
    public async Task Moving_the_shares_between_rafts_before_seeding_keeps_the_total_and_adds_or_removes_cycles()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "10000");
        var done = await client.CompleteNurseryAsync(api, batch.Id(), "9500", new Share(world.Raft1, 4750m), new Share(world.Raft2, 4750m));
        var cycle2 = done.AllocationOf(world.Raft2).GetProperty("cycleId").GetGuid();

        // All into one raft: the other raft loses its cycle (it has no seed and no record).
        var one = (await (await client.PostAllocationsAsync(api, batch.Id(), [new Share(world.Raft1, 9500m, 700m)])).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");
        Assert.Equal((9500m, 1), (one.AllocationOf(world.Raft1).Pieces("quantity"), one.GetProperty("allocations").GetArrayLength()));
        Assert.False(await api.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.Id == cycle2)));
        Assert.Equal((9500m, 700m), await api.QueryAsync(async db => { var c = await db.CultivationCycles.SingleAsync(c => c.NurseryBatchId == batch.Id()); return (c.NurseryAllocatedQuantity!.Value, c.TargetYieldKg!.Value); }));

        // Seed some, then split again: the share of a raft is not below what is hung on it and a raft with seed stays.
        var cycle1 = one.AllocationOf(world.Raft1).GetProperty("cycleId").GetGuid();
        await client.SeedingRecordAsync(cycle1, NurseryTestKit.NurserySession([1], 3000m));
        var below = await (await client.PostAllocationsAsync(api, batch.Id(), [new Share(world.Raft1, 2000m), new Share(world.Raft2, 7500m)])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_ALLOCATION_BELOW_SEEDED", below.Text("reasonCode"));
        var removed = await (await client.PostAllocationsAsync(api, batch.Id(), [new Share(world.Raft2, 9500m)])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_ALLOCATION_BELOW_SEEDED", removed.Text("reasonCode"));
        Assert.Equal(["Allocations"], await (await client.PostAllocationsAsync(api, batch.Id(), [new Share(world.Raft1, 5000m), new Share(world.Raft2, 4000m)])).InvalidFieldsAsync());

        var split = (await (await client.PostAllocationsAsync(api, batch.Id(), [new Share(world.Raft1, 3000m), new Share(world.Raft2, 6500m)])).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");
        Assert.Equal((3000m, 0m, 6500m, 2), (split.AllocationOf(world.Raft1).Pieces("quantity"), split.AllocationOf(world.Raft1).Pieces("remaining"), split.AllocationOf(world.Raft2).Pieces("quantity"), split.GetProperty("allocations").GetArrayLength()));
        Assert.Equal(2, (await api.NurseryCyclesAsync(batch.Id())).Count);

        var history = (await client.NurseryHistoryAsync(batch.Id())).GetProperty("entries").EnumerateArray().Select(e => e.Text("action")).ToList();
        Assert.Equal(["Created", "Completed", "AllocationsChanged", "AllocationsChanged"], history);
    }

    [Fact]
    public async Task The_nursery_actions_are_refused_before_the_completion_and_for_a_zone_manager_of_another_zone()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");
        var outsider = await api.ManagerAsync((await client.CreateZoneAsync()).Id());

        var early = await (await client.PostSurvivorsAsync(api, batch.Id(), 900m, "Sớm")).ReadAsync(HttpStatusCode.Conflict);
        var move = await (await client.PostAllocationsAsync(api, batch.Id(), [new Share(world.Raft1, 1000m)])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_NOT_COMPLETED", "NURSERY_NOT_COMPLETED"), (early.Text("reasonCode"), move.Text("reasonCode")));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.PostCompleteAsync(api, batch.Id(), "900", [new Share(world.Raft1, 900m)])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.PostAllocationsAsync(api, batch.Id(), [new Share(world.Raft1, 1000m)])).StatusCode);
        Assert.Equal(StatusIds.NurseryBatch.Nursing, (await client.GetNurseryAsync(batch.Id())).GetProperty("statusId").GetInt32());
    }
}
