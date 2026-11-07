using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-03 parallel saves on SQL Server. No race may end in a 500 or leave a usage written into a cycle whose closer did not see it (BR-CYCLE-04).</summary>
public class MaterialUsageRaceTests
{
    /// <summary>A host with an Owner, a zone and an item of the group of materials.</summary>
    internal static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId, Guid ItemId)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        return (host, owner, (await owner.CreateZoneAsync()).Id(), await host.ItemAsync());
    }

    /// <summary>The close of C5 as far as this save goes: reads the row and saves it with the version it read. False when another save changed the row first.</summary>
    private static async Task<bool> CloseAsync(FarmingSqlServerHost host, Guid cycleId)
    {
        await using var db = host.Database.CreateContext();
        var cycle = await db.CultivationCycles.SingleAsync(c => c.Id == cycleId);
        (cycle.StatusId, cycle.CloseDate) = (StatusIds.CultivationCycle.Closed, Today);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static async Task AssertAnsweredAsync(HttpResponseMessage answer, params HttpStatusCode[] allowed)
    {
        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(allowed.Contains(answer.StatusCode), $"{(int)answer.StatusCode}: {text}");
    }

    // The close read the cycle before the record was saved: it cannot save over it, so no record enters a cycle whose closer did not see it.
    [SqlServerFact]
    public async Task A_close_that_read_the_cycle_before_a_record_loses_to_it_and_one_that_saved_first_refuses_it()
    {
        var (host, owner, zoneId, item) = await StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycle = await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)));

        await using (var closing = host.Database.CreateContext())
        {
            var read = await closing.CultivationCycles.SingleAsync(c => c.Id == cycle.Id());
            var record = await owner.RecordUsageAsync(raftId, item);
            (read.StatusId, read.CloseDate) = (StatusIds.CultivationCycle.Closed, Today);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => closing.SaveChangesAsync());

            // The same for an edit, whose record leaves the dates of the cycle.
            closing.ChangeTracker.Clear();
            var again = await closing.CultivationCycles.SingleAsync(c => c.Id == cycle.Id());
            await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { UsageDate = Today.AddDays(-30) })).ReadAsync(HttpStatusCode.OK);
            (again.StatusId, again.CloseDate) = (StatusIds.CultivationCycle.Closed, Today);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => closing.SaveChangesAsync());
        }

        Assert.True(await CloseAsync(host, cycle.Id()));
        var refused = await (await owner.PostUsageAsync(raftId, new UsageBody(Today, "x", [new UsageLine(item)]))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
    }

    [SqlServerFact]
    public async Task A_record_racing_a_close_answers_2xx_or_409_and_never_500()
    {
        var (host, owner, zoneId, item) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < 12; round++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
            var cycle = await owner.StartCycleAsync(raftId);

            var post = owner.PostUsageAsync(raftId, new UsageBody(Today, "race", [new UsageLine(item)]));
            var close = CloseAsync(host, cycle.Id());
            await Task.WhenAll(post, close);

            await AssertAnsweredAsync(await post, HttpStatusCode.Created, HttpStatusCode.Conflict);
            var saved = (await post).StatusCode == HttpStatusCode.Created;
            Assert.Equal(saved ? 1 : 0, await host.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == raftId)));
        }
    }

    [SqlServerFact]
    public async Task An_edit_racing_a_close_answers_2xx_or_409_and_never_500()
    {
        var (host, owner, zoneId, item) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < 12; round++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
            var cycle = await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)));
            var record = await owner.RecordUsageAsync(raftId, item, Today.AddDays(-1));

            var put = owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 9m });
            var close = CloseAsync(host, cycle.Id());
            await Task.WhenAll(put, close);

            await AssertAnsweredAsync(await put, HttpStatusCode.OK, HttpStatusCode.Conflict);
        }
    }

    // GBR-01 in a race: on a date with no cycle the raft row decides.
    [SqlServerFact]
    public async Task A_record_racing_a_discontinue_of_the_raft_answers_2xx_or_409_and_never_500()
    {
        var (host, owner, zoneId, item) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < 8; round++)
        {
            var raft = await owner.CreateRaftAsync(zoneId);

            var post = owner.PostUsageAsync(raft.Id(), new UsageBody(Today, "race", [new UsageLine(item)]));
            var discontinue = owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { reason = "old", rowVersion = raft.Text("rowVersion") });
            await Task.WhenAll(post, discontinue);

            await AssertAnsweredAsync(await post, HttpStatusCode.Created, HttpStatusCode.Conflict);
            await AssertAnsweredAsync(await discontinue, HttpStatusCode.OK, HttpStatusCode.Conflict);
            Assert.False((await post).StatusCode == HttpStatusCode.Created && (await discontinue).StatusCode == HttpStatusCode.OK, "A usage and a discontinue on the version both saved.");
        }
    }

    [SqlServerFact]
    public async Task Parallel_records_on_one_cycle_each_save_whole_or_answer_409_with_one_audit_row_per_record()
    {
        var (host, owner, zoneId, item) = await StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        await owner.StartCycleAsync(raftId);
        var other = await host.ItemAsync();

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            owner.PostUsageAsync(raftId, new UsageBody(Today, "race", [new UsageLine(item), new UsageLine(other)]))));

        foreach (var answer in answers)
        {
            await AssertAnsweredAsync(answer, HttpStatusCode.Created, HttpStatusCode.Conflict);
        }

        var saved = answers.Count(a => a.StatusCode == HttpStatusCode.Created);
        Assert.True(saved >= 1);
        Assert.Equal(saved * 2, await host.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == raftId)));
        Assert.Equal(saved * 2, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "MaterialUsage.Created")));
    }

    [SqlServerFact]
    public async Task Parallel_edits_of_one_record_from_one_version_save_once()
    {
        var (host, owner, zoneId, item) = await StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var record = await owner.RecordUsageAsync(raftId, item);

        var answers = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 10m + i })));

        foreach (var answer in answers)
        {
            await AssertAnsweredAsync(answer, HttpStatusCode.OK, HttpStatusCode.Conflict);
        }

        Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "MaterialUsage.Updated" && e.RecordId == record.Id())));
    }
}
