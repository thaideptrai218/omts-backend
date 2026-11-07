using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of PR C3 on SQL Server: real row versions, parallel writes that must never answer 500, column limits and constraints.</summary>
public class MaterialUsagesQaTestsSqlServer
{
    private static async Task AssertAnsweredAsync(HttpResponseMessage answer, params HttpStatusCode[] allowed)
    {
        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(allowed.Contains(answer.StatusCode), $"{(int)answer.StatusCode}: {text}");
        if (answer.StatusCode == HttpStatusCode.Conflict)
        {
            Assert.Contains(await answer.ErrorCodeAsync(), new[] { "MSG06", "MSG-GEN-09", "MSG-GEN-06" });
        }
    }

    // On SQL Server's own date comparison and collation: boundary days, the order of the list, the lock, and a move across zones.
    [SqlServerFact]
    public async Task The_closed_days_the_order_and_a_move_across_zones_hold_on_sql_server()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var closed = new CultivationCycle { RaftId = raftId, StartDate = Today.AddDays(-30), CloseDate = Today.AddDays(-20), StatusId = StatusIds.CultivationCycle.Closed };
        await host.UseDbAsync(db => db.CultivationCycles.Add(closed));
        var open = await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)));
        await host.UseDbAsync(db => db.MaterialUsages.Add(new MaterialUsage
        {
            RaftId = raftId, ItemId = item, UsageDate = Today.AddDays(-25), RepairReason = "old", Quantity = 1.5m, RecordedByAccountId = host.Owner.Account.Id,
        }));

        foreach (var date in new[] { Today.AddDays(-30), Today.AddDays(-20) })
        {
            var refused = await (await owner.PostUsageAsync(raftId, new UsageBody(date, "x", [new UsageLine(item)]))).ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(closed.Id, refused.GetProperty("cycleId").GetGuid());
        }

        await owner.RecordUsageAsync(raftId, item, Today.AddDays(-31));
        await owner.RecordUsageAsync(raftId, item, Today.AddDays(-19));
        var inOpen = await owner.RecordUsageAsync(raftId, item, Today.AddDays(-10));

        var list = await owner.UsageListAsync(raftId);
        Assert.Equal([(Guid?)open.Id(), closed.Id, null], list.GroupsOf().Select(g => g.Cycle));
        Assert.Equal([false, true, false], list.GetProperty("groups").EnumerateArray().Select(g => g.Flag("locked")));
        Assert.Equal(
            [Today.AddDays(-19), Today.AddDays(-31)],
            list.GetProperty("groups")[2].GetProperty("repairs").EnumerateArray().Select(r => r.Date("usageDate")));

        // A move across zones moves the version of the rows at both ends, and the row of the raft it lands on.
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var (cycleBefore, raftBBefore) = ((await CycleAsync(owner, open.Id())).Text("rowVersion"), (await RaftAsync(owner, raftB)).Text("rowVersion"));
        var moved = await (await owner.PutUsageAsync(inOpen.Id(), inOpen.AsEdit("wrong raft") with { RaftId = raftB })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(raftB, moved.GetProperty("record").GetProperty("raftId").GetGuid());
        Assert.NotEqual(cycleBefore, (await CycleAsync(owner, open.Id())).Text("rowVersion"));
        Assert.NotEqual(raftBBefore, (await RaftAsync(owner, raftB)).Text("rowVersion"));
        var row = await host.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "MaterialUsage.Updated" && e.RecordId == inOpen.Id()));
        Assert.Equal(zoneB, row.ZoneId);
    }

    private static Task<JsonElement> CycleAsync(HttpClient client, Guid cycleId) => client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}");

    private static Task<JsonElement> RaftAsync(HttpClient client, Guid raftId) => client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}");

    // NFR-09 on real row versions: the answer names who saved first and gives the current version; that version then saves.
    [SqlServerFact]
    public async Task A_stale_version_answers_msg06_with_the_current_version_and_that_version_saves()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var manager = await host.StaffInZoneAsync(zoneId);
        var record = await owner.RecordUsageAsync(raftId, item);

        var first = (await (await manager.Client.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 3m })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        var stale = await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 4m })).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG06", stale.Text("code"));
        Assert.Equal(first.Text("rowVersion"), stale.Text("currentRowVersion"));
        Assert.Contains("Zone manager", stale.Text("detail"));
        Assert.Equal(3m, (await owner.GetUsageAsync(record.Id())).GetProperty("quantity").GetDecimal());

        var again = await (await owner.PutUsageAsync(record.Id(), first.AsEdit() with { Quantity = 4m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(4m, again.GetProperty("record").GetProperty("quantity").GetDecimal());
    }

    // The client list: a PUT that changes nothing moves no version, whatever the scale of the number sent; the column gives 2.000 back.
    [SqlServerFact]
    public async Task An_edit_of_the_same_quantity_at_another_scale_changes_nothing()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var record = await owner.RecordUsageAsync(raftId, item, quantity: 2m);
        var raftBefore = (await RaftAsync(owner, raftId)).Text("rowVersion");

        var same = await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 2.000m })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(record.Text("rowVersion"), same.GetProperty("record").Text("rowVersion"));
        Assert.Equal(raftBefore, (await RaftAsync(owner, raftId)).Text("rowVersion"));
        Assert.False(await host.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "MaterialUsage.Updated")));
    }

    [SqlServerFact]
    public async Task The_longest_values_and_vietnamese_text_fit_the_columns_and_the_audit_log()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var repair = new string('ệ', 500);
        var reason = new string('ư', 1000);

        var record = await owner.RecordUsageAsync(raftId, item, Today, 1_000_000_000m, repair);
        Assert.Equal(repair, record.Text("repairReason"));
        Assert.Equal(1_000_000_000m, record.GetProperty("quantity").GetDecimal());

        var edited = (await (await owner.PutUsageAsync(record.Id(), record.AsEdit(reason) with { Quantity = 0.001m, RepairReason = repair[..499] })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");

        Assert.Equal(0.001m, edited.GetProperty("quantity").GetDecimal());
        var row = await host.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "MaterialUsage.Updated"));
        Assert.Equal(reason, row.Reason);
        Assert.Equal(repair[..499], JsonDocument.Parse(row.After!).RootElement.GetProperty("RepairReason").GetString());
        Assert.Equal(0.001m, JsonDocument.Parse(row.After!).RootElement.GetProperty("Quantity").GetDecimal());
    }

    // A crafted list of unknown items is a 400 listing each line, past the 2100 parameters of SQL Server.
    [SqlServerFact]
    public async Task A_very_long_list_of_unknown_items_answers_400()
    {
        var (host, owner, zoneId, _) = await MaterialUsageRaceTests.StartAsync();
        await using var _1 = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();

        var lines = Enumerable.Range(0, 3000).Select(_ => new UsageLine(Guid.NewGuid())).ToList();
        var answer = await owner.PostUsageAsync(raftId, new UsageBody(Today, "x", lines));

        Assert.Equal(3000, (await answer.InvalidFieldsAsync()).Count);
    }

    [SqlServerFact]
    public async Task The_constraint_refuses_zero_and_below_and_accepts_the_smallest_value_and_foreign_keys_hold()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var recorder = host.Owner.Account.Id;

        foreach (var bad in new[] { 0m, -1m, -0.001m })
        {
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.MaterialUsages.Add(new MaterialUsage
            {
                RaftId = raftId, ItemId = item, UsageDate = Today, RepairReason = "x", Quantity = bad, RecordedByAccountId = recorder,
            })));
            Assert.Contains("CK_MaterialUsage_Quantity", refused.InnerException!.Message);
        }

        await host.UseDbAsync(db => db.MaterialUsages.Add(new MaterialUsage { RaftId = raftId, ItemId = item, UsageDate = Today, RepairReason = "x", Quantity = 0.001m, RecordedByAccountId = recorder }));

        var raftDelete = await Assert.ThrowsAsync<SqlException>(() => host.QueryAsync(db => db.Rafts.Where(r => r.Id == raftId).ExecuteDeleteAsync()));

        // A raft has other references too, and SQL Server names whichever it checks first: read this one from the catalog.
        Assert.Contains("REFERENCE constraint", raftDelete.Message);
        Assert.Equal(1, await host.QueryAsync(db => db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = 'FK_MaterialUsages_Rafts_RaftId' AND delete_referential_action = 0").SingleAsync()));
    }

    // BR-CYCLE-01: a cancel may run beside a record; the cycle row decides, and no answer is a 500.
    [SqlServerFact]
    public async Task A_record_racing_a_cancel_answers_2xx_or_409_and_ends_in_a_group_that_exists()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        for (var round = 0; round < 10; round++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
            var cycle = await owner.StartCycleAsync(raftId);

            var post = owner.PostUsageAsync(raftId, new UsageBody(Today, "race", [new UsageLine(item)]));
            var cancel = owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion"));
            await Task.WhenAll(post, cancel);

            await AssertAnsweredAsync(await post, HttpStatusCode.Created, HttpStatusCode.Conflict);
            await AssertAnsweredAsync(await cancel, HttpStatusCode.NoContent, HttpStatusCode.Conflict);
            var saved = (await post).StatusCode == HttpStatusCode.Created;
            Assert.Equal(saved ? 1 : 0, await host.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == raftId)));
            var cycleLeft = await host.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.Id == cycle.Id()));
            Assert.Equal((await cancel).StatusCode != HttpStatusCode.NoContent, cycleLeft);
            if (saved)
            {
                var record = (await (await post).ReadAsync(HttpStatusCode.Created)).GetProperty("records")[0];
                var read = await owner.GetUsageAsync(record.Id());
                Assert.Equal(cycleLeft, !read.IsNull("cycleId"));
            }
        }
    }

    [SqlServerFact]
    public async Task An_edit_racing_a_cancel_or_a_new_start_date_answers_2xx_or_409()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        for (var round = 0; round < 8; round++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
            var cycle = await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)));
            var record = await owner.RecordUsageAsync(raftId, item, Today.AddDays(-1));
            var fresh = await CycleAsync(owner, cycle.Id());

            var put = owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 9m });
            var other = round % 2 == 0
                ? owner.CancelCycleAsync(cycle.Id(), fresh.Text("rowVersion"))
                : owner.PutCycleAsync(cycle.Id(), new CycleBody(Today.AddDays(-2), RowVersion: fresh.Text("rowVersion")));
            await Task.WhenAll(put, other);

            await AssertAnsweredAsync(await put, HttpStatusCode.OK, HttpStatusCode.Conflict);
            await AssertAnsweredAsync(await other, HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.Conflict);
        }
    }

    // GBR-01 in a race at the far end of a move, on a date with no cycle: the row of the raft decides.
    [SqlServerFact]
    public async Task A_move_racing_a_discontinue_of_the_new_raft_never_both_save()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var from = (await owner.CreateRaftAsync(zoneId)).Id();
        for (var round = 0; round < 10; round++)
        {
            var record = await owner.RecordUsageAsync(from, item);
            var to = await owner.CreateRaftAsync(zoneId);

            var put = owner.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = to.Id() });
            var discontinue = owner.PostAsJsonAsync($"/api/rafts/{to.Id()}/discontinue", new { reason = "old", rowVersion = to.Text("rowVersion") });
            await Task.WhenAll(put, discontinue);

            await AssertAnsweredAsync(await put, HttpStatusCode.OK, HttpStatusCode.Conflict);
            await AssertAnsweredAsync(await discontinue, HttpStatusCode.OK, HttpStatusCode.Conflict);
            Assert.False((await put).StatusCode == HttpStatusCode.OK && (await discontinue).StatusCode == HttpStatusCode.OK, "A record moved onto a raft discontinued from the version it saw.");
        }
    }

    // GBR-01 for the zone: a zone with a raft In Use cannot be discontinued, so the raft row covers it. Once the raft is off, the zone goes and nothing can be written.
    [SqlServerFact]
    public async Task A_zone_with_a_raft_in_use_refuses_a_discontinue_and_a_write_after_both_are_off_answers_msg_gen_09()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raft = await owner.CreateRaftAsync(zoneId);
        var record = await owner.RecordUsageAsync(raft.Id(), item);
        var zone = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}");

        var refused = await (await owner.PostAsJsonAsync($"/api/zones/{zoneId}/discontinue", new { reason = "old", rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-06", refused.Text("code"));

        var current = await RaftAsync(owner, raft.Id());
        await (await owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { reason = "old", rowVersion = current.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var zoneNow = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}");
        await (await owner.PostAsJsonAsync($"/api/zones/{zoneId}/discontinue", new { reason = "old", rowVersion = zoneNow.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var post = await owner.PostUsageAsync(raft.Id(), new UsageBody(Today, "x", [new UsageLine(item)]));
        Assert.Equal("MSG-GEN-09", (await post.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        var put = await owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 3m });
        Assert.Equal("MSG-GEN-09", (await put.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    // Two people each correcting a wrong raft, in opposite directions: the rows are written in another order, the classic deadlock. A victim answers 409, never 500.
    [SqlServerFact]
    public async Task Moves_in_opposite_directions_with_parallel_reads_never_answer_500()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var (raftA, raftB) = ((await owner.CreateRaftAsync(zoneId)).Id(), (await owner.CreateRaftAsync(zoneId)).Id());
        var cycleB = await owner.StartCycleAsync(raftB, new CycleBody(Today.AddDays(-2)));
        Assert.NotEqual(Guid.Empty, cycleB.Id());

        for (var round = 0; round < 12; round++)
        {
            var (inA, inB) = (await owner.RecordUsageAsync(raftA, item), await owner.RecordUsageAsync(raftB, item));

            var answers = await Task.WhenAll(
                owner.PutUsageAsync(inA.Id(), inA.AsEdit() with { RaftId = raftB }),
                owner.PutUsageAsync(inB.Id(), inB.AsEdit() with { RaftId = raftA }),
                owner.GetAsync($"/api/rafts/{raftA}/material-usages"),
                owner.GetAsync($"/api/rafts/{raftB}/material-usages"),
                owner.GetAsync($"/api/material-usages/{inA.Id()}"));

            foreach (var answer in answers)
            {
                await AssertAnsweredAsync(answer, HttpStatusCode.OK, HttpStatusCode.Conflict);
            }
        }
    }

    // Records and lists at the same time, on one raft: reads hold their own locks, and one of them can be a deadlock victim outside the save.
    [SqlServerFact]
    public async Task Parallel_records_and_lists_on_one_raft_never_answer_500()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)));
        var manager = await host.StaffInZoneAsync(zoneId);

        for (var round = 0; round < 6; round++)
        {
            var calls = new List<Task<HttpResponseMessage>>();
            for (var i = 0; i < 6; i++)
            {
                calls.Add(owner.PostUsageAsync(raftId, new UsageBody(Today.AddDays(-i), $"r{i}", [new UsageLine(item)])));
                calls.Add(manager.Client.PostUsageAsync(raftId, new UsageBody(Today.AddDays(-10 - i), $"s{i}", [new UsageLine(item)])));
                calls.Add(manager.Client.GetAsync($"/api/rafts/{raftId}/material-usages"));
            }

            foreach (var answer in await Task.WhenAll(calls))
            {
                await AssertAnsweredAsync(answer, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Conflict);
            }
        }
    }

    // BR-CYCLE-03 "duplicate items under a race": the row of the cycle lets one of two overlapping saves through. Pinned, not graded: P1.
    [SqlServerFact]
    public async Task P1_identical_records_in_parallel_save_whole_or_answer_409_and_those_that_saved_stand_side_by_side()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => owner.PostUsageAsync(raftId, new UsageBody(Today, "same", [new UsageLine(item)]))));

        foreach (var answer in answers)
        {
            await AssertAnsweredAsync(answer, HttpStatusCode.Created, HttpStatusCode.Conflict);
        }

        var saved = answers.Count(a => a.StatusCode == HttpStatusCode.Created);
        Assert.True(saved >= 1);
        Assert.Equal(saved, await host.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == raftId)));
        Assert.Equal(saved, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "MaterialUsage.Created")));
    }

    // Client list §2 and NFR-09 "two users edit the same record". Pinned, not graded: P2. What a record does to an open form of the cycle or the raft, each way round.
    [SqlServerFact]
    public async Task P2_a_record_refuses_an_open_cycle_form_or_raft_form_though_nobody_edited_them()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raft = await owner.CreateRaftAsync(zoneId);
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-5)));
        var (cycleForm, raftForm) = (await CycleAsync(owner, cycle.Id()), await RaftAsync(owner, raft.Id()));
        await owner.RecordUsageAsync(raft.Id(), item, Today.AddDays(-1));

        var onCycle = await (await owner.PutCycleAsync(cycle.Id(), new CycleBody(Today.AddDays(-5), 5m, RowVersion: cycleForm.Text("rowVersion")))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG06", onCycle.Text("code"));

        // The raft form stays valid after a record in the cycle.
        var unchanged = await RaftAsync(owner, raft.Id());
        Assert.Equal(raftForm.Text("rowVersion"), unchanged.Text("rowVersion"));

        // On a date with no cycle the raft form is the one refused, and an edit of the record alone does it again.
        var free = await owner.RecordUsageAsync(raft.Id(), item, Today.AddDays(-20));
        Assert.NotEqual(raftForm.Text("rowVersion"), (await RaftAsync(owner, raft.Id())).Text("rowVersion"));
        var version = (await RaftAsync(owner, raft.Id())).Text("rowVersion");
        await (await owner.PutUsageAsync(free.Id(), free.AsEdit() with { Quantity = 8m })).ReadAsync(HttpStatusCode.OK);
        Assert.NotEqual(version, (await RaftAsync(owner, raft.Id())).Text("rowVersion"));
    }

    // NFR-10: a list of a raft within 2 seconds at p95, with far more records than a raft will have. One client, so a floor and not the load test of the NFR.
    [SqlServerFact]
    public async Task The_list_of_a_raft_with_a_thousand_records_answers_within_two_seconds()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var recorder = host.Owner.Account.Id;
        await host.UseDbAsync(db => db.MaterialUsages.AddRange(Enumerable.Range(0, 1000).Select(i => new MaterialUsage
        {
            RaftId = raftId, ItemId = item, UsageDate = Today.AddDays(-(i % 200)), RepairReason = $"repair {i % 7}", Quantity = 1m, RecordedByAccountId = recorder,
        })));

        var times = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            (await owner.GetAsync($"/api/rafts/{raftId}/material-usages")).EnsureSuccessStatusCode();
            times.Add(watch.ElapsedMilliseconds);
        }

        Assert.True(times.Order().ElementAt(18) < 2000, $"p95 {times.Order().ElementAt(18)} ms");
    }
}
