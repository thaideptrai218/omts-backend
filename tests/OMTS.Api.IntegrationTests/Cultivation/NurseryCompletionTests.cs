using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// NURSERY-03 to NURSERY-05 (v2, 13/11/2026): completing a nursery records only the survivors, the date and 1 to 3 photos (no shares, no cycle); the batch is then a pool "đã ươm xong" the cycles seed from. The
/// survivors can be corrected (not below what is seeded) and the batch closed, which writes the pieces left in the pool off as an estimated loss. Each test starts its own host, as it counts files and notices.
/// </summary>
public sealed class NurseryCompletionTests
{
    // 10 000 pieces in, 9 500 survive: the batch is "đã ươm xong", its pool is 9 500, nothing is seeded, and NO cycle is created (the old shares are gone).
    [Fact]
    public async Task Completing_a_nursery_records_the_survivors_and_the_photos_only_and_creates_no_cycle()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "10000");

        var done = (await (await client.PostCompleteAsync(api, batch.Id(), "9500", [Jpeg, Png])).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((StatusIds.NurseryBatch.Completed, "Completed", "Đã ươm xong", Today, 9500m, 500m, 5.0m, true),
            (done.GetProperty("statusId").GetInt32(), done.Text("status"), done.Text("statusLabel"), done.Date("completedOn"), done.Pieces("survivors"), done.Pieces("lossPieces"), done.Pieces("lossPercent"), done.Flag("estimated")));
        Assert.Equal((0m, 9500m), (done.Pieces("seeded"), done.Pieces("remaining")));
        Assert.Equal(2, done.GetProperty("completionPhotos").GetArrayLength());
        Assert.Equal(2, api.StoredFiles(FileCategories.NurseryCompletionPhotos).Count);
        Assert.True(done.Flag("canCorrectSurvivors") && done.Flag("canClose") && !done.Flag("canEdit") && !done.Flag("canComplete"));
        Assert.Empty(done.GetProperty("usage").EnumerateArray());
        Assert.False(done.TryGetProperty("allocations", out _));
        Assert.Equal(JsonValueKind.Null, done.GetProperty("closedOn").ValueKind);

        // No cycle and no allocation row: the old completion made one per raft.
        Assert.Equal(0, await api.QueryAsync(db => db.CultivationCycles.CountAsync()));
        Assert.Equal(0, await api.QueryAsync(db => db.NurseryBatchAllocations.CountAsync()));

        // One audit row for the completion, the history shows the survivors from nothing to 9 500 with the new label.
        var history = await client.NurseryHistoryAsync(batch.Id());
        Assert.Equal(["Created", "Completed"], history.GetProperty("entries").EnumerateArray().Select(e => e.Text("action")).ToList());
        var completion = history.GetProperty("entries")[1];
        Assert.Equal("Ươm xong", completion.Text("actionLabel"));
        var change = completion.GetProperty("changes").EnumerateArray().Single(c => c.Text("field") == "Survivors");
        Assert.Equal(("Số mảnh sống sót (ước tính)", "9500"), (change.Text("label"), change.Text("to")));
        Assert.Equal(JsonValueKind.Null, change.GetProperty("from").ValueKind);

        // The list and the detail carry the pool; the available list offers the batch for a cycle.
        var item = Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneId={world.ZoneId}&status=completed")).GetProperty("items").EnumerateArray());
        Assert.Equal((batch.Id(), 9500m, 0m, 9500m), (item.Id(), item.Pieces("survivors"), item.Pieces("seeded"), item.Pieces("remaining")));
        var offered = Assert.Single((await client.AvailableNurseriesAsync(world.ZoneId)).GetProperty("batches").EnumerateArray());
        Assert.Equal((batch.Text("code"), 9500m, 0m, 9500m, world.Lot.Code), (offered.Text("code"), offered.Pieces("survivors"), offered.Pieces("seeded"), offered.Pieces("remaining"), offered.Text("seedLotCode")));
    }

    [Fact]
    public async Task A_field_sent_for_the_old_shares_is_ignored()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var batch = await world.Owner.Client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");
        var form = new MultipartFormDataContent
        {
            { new StringContent(await api.IssueRowVersionAsync<NurseryBatch>(batch.Id())), "rowVersion" },
            { new StringContent("900"), "survivors" },
            { new StringContent($"[{{\"raftId\":\"{world.Raft1}\",\"quantity\":900}}]"), "allocations" },
            { FileContent(Jpeg, "image/jpeg"), "photos", "done.jpg" },
        };

        var done = (await (await world.Owner.Client.PostAsync($"/api/nursery-batches/{batch.Id()}/complete", form)).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal(900m, done.Pieces("remaining"));
        Assert.Equal(0, await api.QueryAsync(db => db.CultivationCycles.CountAsync()));
    }

    [Fact]
    public async Task A_nursery_where_nothing_survived_completes_with_nothing_to_seed_and_nothing_to_close()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");

        var done = (await (await client.PostCompleteAsync(api, batch.Id(), "0")).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((0m, 1000m, 100.0m, 0m), (done.Pieces("survivors"), done.Pieces("lossPieces"), done.Pieces("lossPercent"), done.Pieces("remaining")));
        Assert.False(done.Flag("canClose"));
        Assert.Empty((await client.AvailableNurseriesAsync(world.ZoneId)).GetProperty("batches").EnumerateArray());
        var nothing = await (await client.PostCloseAsync(api, batch.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_NOTHING_TO_CLOSE", "MSG-GEN-09"), (nothing.Text("reasonCode"), nothing.Text("code")));
    }

    [Fact]
    public async Task A_completion_that_is_refused_saves_no_photo_and_keeps_the_batch_nursing()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.StartNurseryAsync(world.ZoneId, world.Lot, "10000");

        Assert.Equal(["Survivors"], await (await client.PostCompleteAsync(api, batch.Id(), "10001")).InvalidFieldsAsync());
        Assert.Equal(["Survivors"], await (await client.PostCompleteAsync(api, batch.Id(), null)).InvalidFieldsAsync());
        Assert.Equal(["Survivors"], await (await client.PostCompleteAsync(api, batch.Id(), "-1")).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", [])).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", [Jpeg, Jpeg, Jpeg, Jpeg])).InvalidFieldsAsync());
        Assert.Equal(["Completed on"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", null, Today.AddDays(-30).ToString("yyyy-MM-dd"))).InvalidFieldsAsync());
        Assert.Equal(["Completed on"], await (await client.PostCompleteAsync(api, batch.Id(), "9500", null, Today.AddDays(1).ToString("yyyy-MM-dd"))).InvalidFieldsAsync());

        var current = await client.GetNurseryAsync(batch.Id());
        Assert.Equal((StatusIds.NurseryBatch.Nursing, 0), (current.GetProperty("statusId").GetInt32(), current.GetProperty("completionPhotos").GetArrayLength()));
        Assert.Empty(api.StoredFiles(FileCategories.NurseryCompletionPhotos));
    }

    [Fact]
    public async Task A_zone_manager_completes_and_the_owner_is_told_a_second_completion_is_refused()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var manager = await api.ManagerAsync(world.ZoneId);
        var batch = await manager.Client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");

        await manager.Client.CompleteNurseryAsync(api, batch.Id(), "900");

        Assert.Equal(2, (await api.NurseryNoticesAsync(batch.Id())).Count(n => n.RecipientAccountId == world.Owner.Account.Id)); // started and completed
        var again = await (await manager.Client.PostCompleteAsync(api, batch.Id(), "900")).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_NOT_NURSING", again.Text("reasonCode"));
    }

    [Fact]
    public async Task Correcting_the_survivors_is_kept_old_to_new_with_the_reason_and_never_goes_below_what_is_seeded()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");

        // Not above what went in, and a reason is needed.
        Assert.Equal(["Survivors"], await (await client.PostSurvivorsAsync(api, batch.Id(), 10001m, "Đếm lại")).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await client.PostSurvivorsAsync(api, batch.Id(), 9200m, " ")).InvalidFieldsAsync());

        var corrected = (await (await client.PostSurvivorsAsync(api, batch.Id(), 9200m, "Đếm lại")).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((9200m, 800m, 9200m), (corrected.Pieces("survivors"), corrected.Pieces("lossPieces"), corrected.Pieces("remaining")));
        var entry = (await client.NurseryHistoryAsync(batch.Id())).GetProperty("entries").EnumerateArray().Single(e => e.Text("action") == "SurvivorsCorrected");
        Assert.Equal(("Sửa số sống sót", "Đếm lại"), (entry.Text("actionLabel"), entry.Text("reason")));
        var survivors = entry.GetProperty("changes").EnumerateArray().Single(c => c.Text("field") == "Survivors");
        Assert.Equal(("9500", "9200"), (survivors.Text("from"), survivors.Text("to")));

        // The same number changes nothing (no second history entry); with 3 000 seeded the survivors cannot go below them, but can be lowered down to them.
        await (await client.PostSurvivorsAsync(api, batch.Id(), 9200m, "Như cũ")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, (await client.NurseryHistoryAsync(batch.Id())).GetProperty("entries").EnumerateArray().Count(e => e.Text("action") == "SurvivorsCorrected"));
        var cycle = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());
        await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([1], 3000m));
        var below = await (await client.PostSurvivorsAsync(api, batch.Id(), 2999m, "Đếm lại")).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_SURVIVORS_BELOW_SEEDED", 3000m), (below.Text("reasonCode"), below.Pieces("seeded")));
        var lowest = (await (await client.PostSurvivorsAsync(api, batch.Id(), 3000m, "Chỉ còn từng ấy")).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");
        Assert.Equal((3000m, 0m), (lowest.Pieces("survivors"), lowest.Pieces("remaining")));
        Assert.False(lowest.Flag("canClose"));
    }

    // 10 000 in, 9 500 survive, 3 000 seeded: closing writes the 6 500 left off as a loss estimate; nothing is seeded from the batch any more and no cycle can start from it.
    [Fact]
    public async Task Closing_a_nursery_writes_the_pieces_left_off_as_a_loss_and_ends_the_pool()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var cycle = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());
        await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([1, 2], 3000m));

        var closed = (await (await client.PostCloseAsync(api, batch.Id(), "Đủ giống cho các bè")).ReadAsync(HttpStatusCode.OK)).GetProperty("batch");

        Assert.Equal((4004, "Closed", "Đã đóng", Today, 6500m, 0m, 3000m), (closed.GetProperty("statusId").GetInt32(), closed.Text("status"), closed.Text("statusLabel"), closed.Date("closedOn"),
            closed.Pieces("closeLossPieces"), closed.Pieces("remaining"), closed.Pieces("seeded")));
        // The estimated loss is what went in less the survivors (500) plus what the close wrote off (6 500).
        Assert.Equal((7000m, 70.0m, true), (closed.Pieces("lossPieces"), closed.Pieces("lossPercent"), closed.Flag("estimated")));
        Assert.False(closed.Flag("canClose") || closed.Flag("canCorrectSurvivors"));
        var usage = Assert.Single(closed.GetProperty("usage").EnumerateArray());
        Assert.Equal((cycle.Id(), world.Raft1, 3000m, Today), (usage.GetProperty("cycleId").GetGuid(), usage.GetProperty("raftId").GetGuid(), usage.Pieces("seeded"), usage.Date("lastSeedingOn")));

        var entry = (await client.NurseryHistoryAsync(batch.Id())).GetProperty("entries").EnumerateArray().Last();
        Assert.Equal(("Closed", "Đóng đợt ươm", "Đủ giống cho các bè"), (entry.Text("action"), entry.Text("actionLabel"), entry.Text("reason")));

        // Nothing more: not offered, not seeded, no cycle, no second close, no survivors correction.
        Assert.Empty((await client.AvailableNurseriesAsync(world.ZoneId)).GetProperty("batches").EnumerateArray());
        var seed = await (await client.PostSeedingAsync(cycle.Id(), NurseryTestKit.NurserySession([3], 1m))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_POOL_EXCEEDED", 0m), (seed.Text("reasonCode"), seed.Pieces("remaining")));
        var start = await (await client.PostNurseryCycleAsync(world.Raft2, batch.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_NOT_COMPLETED", start.Text("reasonCode"));
        Assert.Equal("NURSERY_NOT_COMPLETED", (await (await client.PostCloseAsync(api, batch.Id())).ReadAsync(HttpStatusCode.Conflict)).Text("reasonCode"));
        Assert.Equal("NURSERY_NOT_COMPLETED", (await (await client.PostSurvivorsAsync(api, batch.Id(), 9000m, "Sau khi đóng")).ReadAsync(HttpStatusCode.Conflict)).Text("reasonCode"));

        // The list counts it as done nursing, with the loss of the close in the totals, and can filter on it.
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneId={world.ZoneId}&status=closed");
        Assert.Equal(batch.Id(), Assert.Single(list.GetProperty("items").EnumerateArray()).Id());
        var summary = list.GetProperty("summary");
        Assert.Equal((10000m, 9500m, 7000m, 70.0m, 1, 1), (summary.Pieces("completedQuantityIn"), summary.Pieces("survivors"), summary.Pieces("lossPieces"), summary.Pieces("lossPercent"),
            summary.GetProperty("completedCount").GetInt32(), summary.GetProperty("closedCount").GetInt32()));
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>($"/api/nursery-batches?zoneId={world.ZoneId}&status=completed")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_zone_manager_closing_a_nursery_tells_the_owner_and_the_owner_closing_tells_nobody()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var manager = await api.ManagerAsync(world.ZoneId);
        var byManager = await world.Owner.Client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "1000", "900");
        var byOwner = await world.Owner.Client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "1000", "900");

        await (await manager.Client.PostCloseAsync(api, byManager.Id())).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.Client.PostCloseAsync(api, byOwner.Id())).ReadAsync(HttpStatusCode.OK);

        Assert.Single(await api.NurseryNoticesAsync(byManager.Id()), n => n.RecipientAccountId == world.Owner.Account.Id && n.Title == "Nursery closed");
        Assert.DoesNotContain(await api.NurseryNoticesAsync(byOwner.Id()), n => n.Title == "Nursery closed");
    }

    [Fact]
    public async Task The_usage_lists_every_cycle_that_seeded_from_the_batch_the_one_with_the_most_first()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var first = await client.StartNurseryCycleAsync(world.Raft1, batch.Id());
        var second = await client.StartNurseryCycleAsync(world.Raft2, batch.Id());
        await client.SeedingRecordAsync(first.Id(), NurseryTestKit.NurserySession([1], 1000m));
        await client.SeedingRecordAsync(second.Id(), NurseryTestKit.NurserySession([1], 4000m));
        await client.SeedingRecordAsync(first.Id(), NurseryTestKit.NurserySession([2], 500m));

        var view = await client.GetNurseryAsync(batch.Id());

        Assert.Equal((5500m, 4000m), (view.Pieces("seeded"), view.Pieces("remaining")));
        var usage = view.GetProperty("usage").EnumerateArray().ToList();
        Assert.Equal([(world.Raft2, 4000m), (world.Raft1, 1500m)], usage.Select(u => (u.GetProperty("raftId").GetGuid(), u.Pieces("seeded"))).ToList());
        Assert.All(usage, u => Assert.Equal(("Open", Today), (u.Text("cycleStatus"), u.Date("lastSeedingOn"))));
    }

    [Fact]
    public async Task The_transfer_of_shares_between_rafts_is_gone()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var batch = await world.Owner.Client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "1000", "900");

        var gone = await world.Owner.Client.PostAsJsonAsync($"/api/nursery-batches/{batch.Id()}/allocations", new { allocations = new[] { new { raftId = world.Raft1, quantity = 900 } }, reason = "x", rowVersion = "" });

        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Equal("NURSERY_ALLOCATIONS_REMOVED", (await gone.Content.ReadFromJsonAsync<JsonElement>()).Text("reasonCode"));
    }

    [Fact]
    public async Task The_available_list_has_only_ready_batches_of_the_zone_with_pieces_left()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var otherZone = (await client.CreateZoneAsync()).Id();
        var ready = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "3000", "2800");
        await client.StartNurseryAsync(world.ZoneId, world.Lot, "2000"); // still nursing
        await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "1000", "0"); // nothing survived
        var elsewhere = await client.ReadyNurseryAsync(api, otherZone, world.Lot, "1500", "1400");
        var manager = await api.ManagerAsync(otherZone);

        Assert.Equal(ready.Id(), Assert.Single((await client.AvailableNurseriesAsync(world.ZoneId)).GetProperty("batches").EnumerateArray()).Id());
        Assert.Equal(2, (await client.AvailableNurseriesAsync(null)).GetProperty("batches").GetArrayLength());
        Assert.Equal(elsewhere.Id(), Assert.Single((await client.AvailableNurseriesAsync(otherZone)).GetProperty("batches").EnumerateArray()).Id());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.GetAsync($"/api/nursery-batches/available?zoneId={world.ZoneId}")).StatusCode);
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
        var close = await (await client.PostCloseAsync(api, batch.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_NOT_COMPLETED", "NURSERY_NOT_COMPLETED"), (early.Text("reasonCode"), close.Text("reasonCode")));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.PostCompleteAsync(api, batch.Id(), "900")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.PostCloseAsync(api, batch.Id())).StatusCode);
        Assert.Equal(StatusIds.NurseryBatch.Nursing, (await client.GetNurseryAsync(batch.Id())).GetProperty("statusId").GetInt32());
    }
}
