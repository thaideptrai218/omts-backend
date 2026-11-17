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
/// CYCLE-01 (v2, 13/11/2026): a cycle is started by hand and only from a nursery batch that is ready (đã ươm xong, same zone as the raft, pieces left in its pool), with the expected total yield and 1 to 3 plan
/// photos; a raft holds one Open cycle; the batch of a cycle can change only before its first seeding record; the yield, the date and the note any time. The old free seed lot path has no route any more.
/// </summary>
public sealed class NurseryCycleStartTests
{
    [Fact]
    public async Task A_cycle_starts_from_a_ready_batch_with_the_yield_and_the_plan_photos_and_shows_the_batch_and_its_seed_lot()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");

        var started = await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), "2000.5", Today.AddDays(-2).ToString("yyyy-MM-dd"), "Vụ hàu đầu tiên", photos: 2)).ReadAsync(HttpStatusCode.Created);
        var cycle = started.GetProperty("cycle");

        Assert.Equal((Today.AddDays(-2), 2000.5m, "Vụ hàu đầu tiên", StatusIds.CultivationCycle.Open), (cycle.Date("startDate"), cycle.Pieces("targetYieldKg"), cycle.Text("note"), cycle.GetProperty("statusId").GetInt32()));
        Assert.Equal((batch.Id(), batch.Text("code"), 0m, 9500m), (cycle.GetProperty("nurseryBatchId").GetGuid(), cycle.Text("nurseryCode"), cycle.Pieces("nurserySeeded"), cycle.Pieces("nurseryRemaining")));
        Assert.Equal((world.Lot.LineId, world.Lot.Code), (cycle.GetProperty("seedLot").Id(), cycle.GetProperty("seedLot").Text("code")));
        Assert.Equal((2, true, false), (cycle.GetProperty("planPhotos").GetArrayLength(), cycle.Flag("canChangeNursery"), cycle.Flag("planPhotosFromNursery")));
        Assert.False(cycle.TryGetProperty("nurseryAllocated", out _));
        Assert.False(cycle.TryGetProperty("expectedHarvestDate", out _));
        Assert.Equal(JsonValueKind.Null, cycle.GetProperty("expectedSeedQuantity").ValueKind);
        Assert.Equal(5, started.GetProperty("lanes").GetArrayLength());
        Assert.Equal(2, api.StoredFiles(FileCategories.CyclePlanPhotos).Count);
        var row = await api.QueryAsync(db => db.CultivationCycles.SingleAsync(c => c.Id == cycle.Id()));
        Assert.Equal((batch.Id(), 2000.5m, (decimal?)null), (row.NurseryBatchId, row.TargetYieldKg, row.NurseryAllocatedQuantity));
        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "CultivationCycle.Started" && e.RecordId == cycle.Id()));
        Assert.Contains(batch.Text("code"), audit.After!);

        // The read, the progress of the raft and the seeding screen show the same batch.
        Assert.Equal(batch.Text("code"), (await client.CycleViewAsync(cycle.Id())).Text("nurseryCode"));
        Assert.Equal(batch.Text("code"), (await client.ProgressAsync(world.Raft1)).GetProperty("cycle").Text("nurseryCode"));
        var list = await client.SeedingListAsync(cycle.Id());
        Assert.Equal((batch.Text("code"), 9500m, 0m, world.Lot.Code), (list.Text("nurseryCode"), list.Pieces("nurseryRemaining"), list.Pieces("nurserySeeded"), list.GetProperty("seedLot").Text("code")));
        Assert.Equal([1, 2, 3, 4, 5], list.Numbers("seedableLaneNumbers"));
    }

    [Fact]
    public async Task The_start_is_also_reachable_with_the_raft_in_the_route_and_one_batch_feeds_several_rafts()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var form = new MultipartFormDataContent
        {
            { new StringContent(batch.Id().ToString()), "nurseryBatchId" },
            { new StringContent("800"), "targetYieldKg" },
            { FileContent(Jpeg, "image/jpeg"), "photos", "plan.jpg" },
        };

        var viaRoute = (await (await client.PostAsync($"/api/rafts/{world.Raft1}/cultivation-cycles", form)).ReadAsync(HttpStatusCode.Created)).GetProperty("cycle");
        var second = await client.StartNurseryCycleAsync(world.Raft2, batch.Id(), "1200");

        Assert.Equal((world.Raft1, batch.Id()), (viaRoute.GetProperty("raftId").GetGuid(), viaRoute.GetProperty("nurseryBatchId").GetGuid()));
        Assert.Equal(batch.Id(), second.GetProperty("nurseryBatchId").GetGuid());
        Assert.Equal(2, await api.QueryAsync(db => db.CultivationCycles.CountAsync(c => c.NurseryBatchId == batch.Id())));
    }

    [Fact]
    public async Task A_start_refuses_a_missing_batch_a_bad_yield_a_bad_date_a_long_note_and_too_few_or_many_photos_and_saves_nothing()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");

        Assert.Equal(["Nursery batch"], await (await client.PostNurseryCycleAsync(world.Raft1, null)).InvalidFieldsAsync());
        Assert.Equal(["Target yield"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), null)).InvalidFieldsAsync());
        Assert.Equal(["Target yield"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), "0")).InvalidFieldsAsync());
        Assert.Equal(["Target yield"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), "abc")).InvalidFieldsAsync());
        Assert.Equal(["Start date"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), startDate: Today.AddDays(1).ToString("yyyy-MM-dd"))).InvalidFieldsAsync());
        Assert.Equal(["Note"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), note: new string('x', 1001))).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), photos: 0)).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await client.PostNurseryCycleAsync(world.Raft1, batch.Id(), photos: 4)).InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostNurseryCycleAsync(world.Raft1, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostNurseryCycleAsync(Guid.NewGuid(), batch.Id())).StatusCode);

        Assert.Equal(0, await api.QueryAsync(db => db.CultivationCycles.CountAsync()));
        Assert.Empty(api.StoredFiles(FileCategories.CyclePlanPhotos));
    }

    [Fact]
    public async Task The_batch_must_be_ready_in_the_zone_of_the_raft_with_pieces_left_and_the_raft_must_have_no_open_cycle()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var nursing = await client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");
        var otherZone = (await client.CreateZoneAsync()).Id();
        var foreign = await client.ReadyNurseryAsync(api, otherZone, world.Lot, "1000", "900");
        var empty = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "1000", "0");
        var ready = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "1000", "900");

        var notReady = await (await client.PostNurseryCycleAsync(world.Raft1, nursing.Id())).ReadAsync(HttpStatusCode.Conflict);
        var otherZoneBatch = await (await client.PostNurseryCycleAsync(world.Raft1, foreign.Id())).ReadAsync(HttpStatusCode.Conflict);
        var nothingLeft = await (await client.PostNurseryCycleAsync(world.Raft1, empty.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("NURSERY_NOT_COMPLETED", "NURSERY_RAFT_OTHER_ZONE", "NURSERY_POOL_EMPTY"), (notReady.Text("reasonCode"), otherZoneBatch.Text("reasonCode"), nothingLeft.Text("reasonCode")));
        Assert.All(new[] { notReady, otherZoneBatch, nothingLeft }, p => Assert.Equal("MSG-GEN-09", p.Text("code")));
        Assert.Equal(0, await api.QueryAsync(db => db.CultivationCycles.CountAsync()));

        // A raft holds one Open cycle: a second start on it is refused and names the cycle.
        var first = await client.StartNurseryCycleAsync(world.Raft1, ready.Id(), "500");
        var second = await (await client.PostNurseryCycleAsync(world.Raft1, ready.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal((first.Id(), "MSG-GEN-06"), (second.GetProperty("cycleId").GetGuid(), second.Text("code")));
        Assert.Equal(1, await api.QueryAsync(db => db.CultivationCycles.CountAsync()));
    }

    [Fact]
    public async Task A_zone_manager_starts_a_cycle_in_the_zone_and_an_accountant_or_a_manager_of_another_zone_cannot()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var manager = await api.ManagerAsync(world.ZoneId);
        var outsider = await api.ManagerAsync((await client.CreateZoneAsync()).Id());
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.PostNurseryCycleAsync(world.Raft1, batch.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.Client.PostNurseryCycleAsync(world.Raft1, batch.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await manager.Client.PostNurseryCycleAsync(world.Raft1, batch.Id())).StatusCode);
    }

    [Fact]
    public async Task The_yield_the_date_and_the_note_change_any_time_while_the_cycle_is_open()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "10000", "9500");
        var cycle = await client.StartNurseryCycleAsync(world.Raft1, batch.Id(), "2000");
        await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([1], 1000m));
        var version = await api.IssueRowVersionAsync<CultivationCycle>(cycle.Id());

        var edited = (await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { targetYieldKg = 2500.25m, startDate = Today.AddDays(-1), note = "Tăng sản lượng", rowVersion = version })).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");

        Assert.Equal((2500.25m, Today.AddDays(-1), "Tăng sản lượng", batch.Id()), (edited.Pieces("targetYieldKg"), edited.Date("startDate"), edited.Text("note"), edited.GetProperty("nurseryBatchId").GetGuid()));
        Assert.False(edited.Flag("canChangeNursery")); // a seeding record locks the batch

        // A field left out stays; the note is cleared by an empty one; a bad yield or date is refused by name.
        var kept = (await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { note = "", rowVersion = version })).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal((2500.25m, Today.AddDays(-1), JsonValueKind.Null), (kept.Pieces("targetYieldKg"), kept.Date("startDate"), kept.GetProperty("note").ValueKind));
        Assert.Equal(["Target yield"], await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { targetYieldKg = 0, rowVersion = version })).InvalidFieldsAsync());
        Assert.Equal(["Start date"], await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { startDate = Today.AddDays(1), rowVersion = version })).InvalidFieldsAsync());
        var audit = await api.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "CultivationCycle.Updated" && e.RecordId == cycle.Id()).ToListAsync());
        Assert.NotEmpty(audit);
    }

    // The batch of a cycle is chosen in the cycle: before the first seeding record it can change to another ready batch, after it 409 CYCLE_SEED_LOCKED; removing the record unlocks it.
    [Fact]
    public async Task The_batch_of_a_cycle_changes_only_before_its_first_seeding_record()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var first = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "5000", "4500");
        var second = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "3000", "2800");
        var nursing = await client.StartNurseryAsync(world.ZoneId, world.Lot, "1000");
        var cycle = await client.StartNurseryCycleAsync(world.Raft1, first.Id(), "2000");

        // Before any record: another ready batch is taken; a batch that is not ready or that is not the cycle's zone is refused.
        var changed = (await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { nurseryBatchId = second.Id(), rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(cycle.Id()) })).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal((second.Id(), second.Text("code"), 2800m), (changed.GetProperty("nurseryBatchId").GetGuid(), changed.Text("nurseryCode"), changed.Pieces("nurseryRemaining")));
        var notReady = await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { nurseryBatchId = nursing.Id(), rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(cycle.Id()) })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("NURSERY_NOT_COMPLETED", notReady.Text("reasonCode"));
        Assert.Equal(second.Id(), (await client.CycleViewAsync(cycle.Id())).GetProperty("nurseryBatchId").GetGuid());
        var history = (await api.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "CultivationCycle.Updated" && e.RecordId == cycle.Id()).ToListAsync())).Single();
        Assert.Contains(first.Text("code"), history.Before!);
        Assert.Contains(second.Text("code"), history.After!);

        // The first seeding record locks the batch; the same batch sent again is no change and passes.
        var record = await client.SeedingRecordAsync(cycle.Id(), NurseryTestKit.NurserySession([1], 500m));
        var locked = await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { nurseryBatchId = first.Id(), rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(cycle.Id()) })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("CYCLE_SEED_LOCKED", "MSG-GEN-09"), (locked.Text("reasonCode"), locked.Text("code")));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { nurseryBatchId = second.Id(), targetYieldKg = 2100, rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(cycle.Id()) })).StatusCode);
        Assert.Equal(second.Id(), (await client.CycleViewAsync(cycle.Id())).GetProperty("nurseryBatchId").GetGuid());

        // With the record removed the cycle can take another batch again.
        await (await client.RemoveSeedingAsync(record.Id(), "Nhập nhầm", await api.IssueRowVersionAsync<SeedingRecord>(record.Id()))).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/cultivation-cycles/{cycle.Id()}", new { nurseryBatchId = first.Id(), rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(cycle.Id()) })).StatusCode);
        Assert.Equal(first.Id(), (await client.CycleViewAsync(cycle.Id())).GetProperty("nurseryBatchId").GetGuid());
    }

    [Fact]
    public async Task A_cycle_of_the_old_way_without_a_nursery_batch_is_still_read_and_edited_the_old_way_and_can_take_a_batch_before_any_seed()
    {
        using var api = new AuthApiFactory();
        var world = await api.NurseryWorldAsync();
        var client = world.Owner.Client;
        var batch = await client.ReadyNurseryAsync(api, world.ZoneId, world.Lot, "1000", "900");
        var old = await client.StartCycleAsync(world.Raft1, new CycleBody(Today.AddDays(-5), 800m, null, 300m));

        Assert.Equal((JsonValueKind.Null, 800m), ((await client.CycleViewAsync(old.Id())).GetProperty("nurseryBatchId").ValueKind, (await client.CycleViewAsync(old.Id())).Pieces("expectedSeedQuantity")));
        var edited = await client.EditCycleAsync(old.Id(), new CycleBody(Today.AddDays(-4), 900m, null, 350m, await api.IssueRowVersionAsync<CultivationCycle>(old.Id())));
        Assert.Equal((900m, 350m), (edited.Pieces("expectedSeedQuantity"), edited.Pieces("targetYieldKg")));

        var taken = (await (await client.PutAsJsonAsync($"/api/cultivation-cycles/{old.Id()}", new { nurseryBatchId = batch.Id(), rowVersion = await api.IssueRowVersionAsync<CultivationCycle>(old.Id()) })).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");
        Assert.Equal((batch.Id(), 350m), (taken.GetProperty("nurseryBatchId").GetGuid(), taken.Pieces("targetYieldKg")));
    }
}
