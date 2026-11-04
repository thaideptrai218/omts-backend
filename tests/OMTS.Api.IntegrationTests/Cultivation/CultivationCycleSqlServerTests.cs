using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-01 on SQL Server: the filtered index, real row versions, and parallel changes of one raft.</summary>
public class CultivationCycleSqlServerTests
{
    private static async Task<string> Show(HttpResponseMessage[] answers)
    {
        var lines = new List<string>();
        foreach (var answer in answers)
        {
            lines.Add($"{(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
        }

        return string.Join(" | ", lines);
    }

    // BR-CYCLE-01: the database itself refuses a second Open cycle, and has no objection to Closed ones.
    [SqlServerFact]
    public async Task The_filtered_index_refuses_a_second_open_cycle_and_allows_closed_ones()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        CultivationCycle Closed(int startDays, int closeDays) => new()
        {
            RaftId = raftId, StartDate = Today.AddDays(startDays), CloseDate = Today.AddDays(closeDays), StatusId = StatusIds.CultivationCycle.Closed,
        };
        await host.UseDbAsync(db => db.CultivationCycles.AddRange(Closed(-40, -30), Closed(-20, -10), new CultivationCycle { RaftId = raftId, StartDate = Today }));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.CultivationCycles.Add(new CultivationCycle { RaftId = raftId, StartDate = Today })));

        Assert.Contains("UX_CultivationCycles_RaftId_Open", failure.InnerException!.Message);
        Assert.Equal(3, await host.QueryAsync(db => db.CultivationCycles.CountAsync(c => c.RaftId == raftId)));
    }

    // Each loser answers as the pre-check does, whether it lost on the raft row, on the index or as a deadlock victim.
    [SqlServerFact]
    public async Task Parallel_starts_leave_one_open_cycle_and_every_other_answer_is_msg_gen_09_naming_it()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();

        for (var round = 0; round < 5; round++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId)).Id();

            var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostCycleAsync(raftId)));

            var shown = await Show(answers);
            var cycle = Assert.Single(await host.QueryAsync(db => db.CultivationCycles.Where(c => c.RaftId == raftId).ToListAsync()));
            Assert.True(answers.Count(a => a.StatusCode == HttpStatusCode.Created) == 1, shown);
            foreach (var lost in answers.Where(a => a.StatusCode != HttpStatusCode.Created))
            {
                var body = await lost.ReadAsync(HttpStatusCode.Conflict);
                Assert.True(body.Text("code") == "MSG-GEN-09", shown);
                Assert.Equal(cycle.Id, body.GetProperty("cycleId").GetGuid());
            }
        }
    }

    // QA of rafts v2: a lane change that read "no open cycle" before the start committed holds a raft row the start has moved, so it cannot save.
    [SqlServerFact]
    public async Task A_lane_change_that_read_no_open_cycle_cannot_save_after_a_start()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id());

        using var scope = host.Api.Services.CreateScope();
        var inProgress = await scope.ServiceProvider.GetRequiredService<IRaftRepository>().GetForEditAsync(raft.Id(), default);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await owner.StartCycleAsync(raft.Id());

        unitOfWork.MarkChanged(inProgress!);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => unitOfWork.SaveChangesAsync());
    }

    // The start moves the row version of the raft: an open raft form is stale, and a lane change from it is refused before anything else.
    [SqlServerFact]
    public async Task A_start_moves_the_row_version_of_the_raft_and_a_cancel_does_not_touch_it()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody(lanes: 3);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);

        var cycle = await owner.StartCycleAsync(raft.Id());

        var moved = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        Assert.NotEqual(raft.Text("rowVersion"), moved.Text("rowVersion"));
        Assert.Equal("MSG06", await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 4, RowVersion = raft.Text("rowVersion") })).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-06", await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 4, RowVersion = moved.Text("rowVersion") })).ErrorCodeAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion"))).StatusCode);
        Assert.Equal(moved.Text("rowVersion"), (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}")).Text("rowVersion"));
        await owner.EditRaftAsync(raft.Id(), body with { LaneCount = 4, RowVersion = moved.Text("rowVersion") });
    }

    [SqlServerFact]
    public async Task A_start_racing_a_lane_change_never_answers_500()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody(lanes: 3);
        var zoneId = (await owner.CreateZoneAsync()).Id();

        for (var round = 0; round < 6; round++)
        {
            var raft = await owner.CreateRaftAsync(zoneId, body with { Code = NewCode() });

            var answers = await Task.WhenAll(
                owner.PostCycleAsync(raft.Id()),
                owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { Code = raft.Text("code"), LaneCount = 5, RowVersion = raft.Text("rowVersion") }));

            var shown = await Show(answers);
            Assert.True(answers.All(a => a.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict), shown);
            foreach (var lost in answers.Where(a => a.StatusCode == HttpStatusCode.Conflict))
            {
                Assert.Contains(await lost.ErrorCodeAsync(), new[] { "MSG06", "MSG-GEN-06" });
            }
        }
    }

    // GBR-01: whichever save comes first, no raft ends Discontinued with an Open cycle.
    [SqlServerFact]
    public async Task A_start_racing_a_discontinue_never_leaves_an_open_cycle_on_a_discontinued_raft()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();

        for (var round = 0; round < 6; round++)
        {
            var raft = await owner.CreateRaftAsync(zoneId);

            var answers = await Task.WhenAll(
                owner.PostCycleAsync(raft.Id()),
                owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") }));

            var shown = await Show(answers);
            Assert.True(answers.All(a => (int)a.StatusCode < 500), shown);
            var saved = await host.QueryAsync(db => db.Rafts.AsNoTracking().SingleAsync(r => r.Id == raft.Id()));
            var open = await host.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raft.Id() && c.StatusId == StatusIds.CultivationCycle.Open));
            Assert.False(saved.StatusId == StatusIds.Usage.Discontinued && open, shown);
            Assert.Equal(answers[0].StatusCode == HttpStatusCode.Created, open);
        }
    }

    [SqlServerFact]
    public async Task Parallel_edits_from_one_row_version_leave_one_winner()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id());
        var cycle = await owner.StartCycleAsync(raft.Id());

        var answers = await Task.WhenAll(new[] { 1m, 2m, 3m }.Select(quantity =>
            owner.PutCycleAsync(cycle.Id(), new CycleBody(Today, quantity, RowVersion: cycle.Text("rowVersion")))));

        var shown = await Show(answers);
        Assert.True(answers.Count(a => a.StatusCode == HttpStatusCode.OK) == 1, shown);
        foreach (var lost in answers.Where(a => a.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (lost.StatusCode, await lost.ErrorCodeAsync()));
        }
    }

    [SqlServerFact]
    public async Task Parallel_cancels_remove_the_cycle_once_and_the_raft_can_start_again()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id());
        var cycle = await owner.StartCycleAsync(raft.Id());

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion"))));

        var shown = await Show(answers);
        Assert.True(answers.Count(a => a.StatusCode == HttpStatusCode.NoContent) == 1, shown);
        Assert.True(answers.All(a => a.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict or HttpStatusCode.NotFound), shown);
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == cycle.Id() && e.Action == "CultivationCycle.Cancelled")));
        await owner.StartCycleAsync(raft.Id());
    }

    // decimal(18,3) and decimal(18,2): the largest value that fits is stored exactly, and one past it is a field error.
    [SqlServerFact]
    public async Task Plan_estimates_up_to_the_size_of_the_columns_are_stored_exactly_and_larger_ones_are_refused()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id());
        var (seed, yield) = (999_999_999_999_999.999m, 9_999_999_999_999_999.99m);

        Assert.Equal(
            ["Expected seed quantity", "Target yield"],
            await (await owner.PostCycleAsync(raft.Id(), new CycleBody(Today, seed + 0.0005m, null, yield + 0.005m))).InvalidFieldsAsync());

        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today, seed, null, yield));

        var saved = await host.QueryAsync(db => db.CultivationCycles.AsNoTracking().SingleAsync(c => c.Id == cycle.Id()));
        Assert.Equal((seed, yield), (saved.ExpectedSeedQuantity, saved.TargetYieldKg));
    }

}
