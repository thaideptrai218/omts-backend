using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>Independent QA of CYCLE-01 and CYCLE-05 on SQL Server: races, real row versions and <see cref="ICultivationCycleQuery"/>.</summary>
public class CyclesQaTestsSqlServer
{
    private static async Task<string> Show(HttpResponseMessage[] answers)
    {
        var lines = new List<string>();
        foreach (var answer in answers)
        {
            lines.Add($"{answer.RequestMessage!.Method} {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
        }

        return string.Join(" | ", lines);
    }

    // PM 03/11: a deadlock victim answers 409 MSG06, never 500. The reads run outside the unit of work, so the API's exception handler maps theirs.
    [SqlServerFact]
    public async Task Starts_and_reads_of_one_raft_in_parallel_never_answer_500()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();

        for (var round = 0; round < 8; round++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
            var calls = new List<Task<HttpResponseMessage>>();
            for (var i = 0; i < 5; i++)
            {
                calls.Add(owner.PostCycleAsync(raftId, new CycleBody(Today.AddDays(-i))));
                calls.Add(owner.GetAsync($"/api/rafts/{raftId}/progress"));
                calls.Add(owner.GetAsync($"/api/rafts/{raftId}/lanes/today"));
                calls.Add(owner.GetAsync($"/api/rafts/{raftId}/cultivation-cycles"));
                calls.Add(owner.GetAsync($"/api/zones/{zoneId}/cultivation-cycles"));
            }

            var answers = await Task.WhenAll(calls);

            var shown = await Show(answers);
            Assert.True(answers.All(a => (int)a.StatusCode < 500), shown);
            Assert.Single(await host.QueryAsync(db => db.CultivationCycles.Where(c => c.RaftId == raftId).ToListAsync()));
        }
    }

    // One Open cycle per raft and GBR-01, whatever wins: a start, an edit, a cancel, a lane change and a raft discontinue from one row version.
    [SqlServerFact]
    public async Task A_cancel_an_edit_a_start_a_lane_change_and_a_discontinue_in_parallel_keep_the_rules_and_never_answer_500()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var body = NewRaftBody(lanes: 3);

        for (var round = 0; round < 6; round++)
        {
            var raft = await owner.CreateRaftAsync(zoneId, body with { Code = AcsTestKit.NewCode() });
            var cycle = await owner.StartCycleAsync(raft.Id());
            var current = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");

            var answers = await Task.WhenAll(
                owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion")),
                owner.PutCycleAsync(cycle.Id(), new CycleBody(Today, 5m, RowVersion: cycle.Text("rowVersion"))),
                owner.PostCycleAsync(raft.Id()),
                owner.PostCycleAsync(raft.Id()),
                owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { Code = raft.Text("code"), LaneCount = 5, RowVersion = current.Text("rowVersion") }),
                owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = current.Text("rowVersion") }));

            var shown = await Show(answers);
            Assert.True(answers.All(a => (int)a.StatusCode < 500), shown);
            var saved = await host.QueryAsync(db => db.Rafts.AsNoTracking().SingleAsync(r => r.Id == raft.Id()));
            var open = await host.QueryAsync(db => db.CultivationCycles.CountAsync(c => c.RaftId == raft.Id() && c.StatusId == StatusIds.CultivationCycle.Open));
            Assert.True(open <= 1, shown);
            Assert.False(saved.StatusId == StatusIds.Usage.Discontinued && open > 0, shown);
        }
    }

    // A zone is discontinued only with no raft In Use, so the raft row of the start guards the zone as well.
    [SqlServerFact]
    public async Task A_start_racing_a_raft_and_a_zone_discontinue_never_leaves_an_open_cycle_in_a_discontinued_zone()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;

        for (var round = 0; round < 6; round++)
        {
            var zone = await owner.CreateZoneAsync();
            var raft = await owner.CreateRaftAsync(zone.Id());
            var currentZone = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}");

            var answers = await Task.WhenAll(
                owner.PostCycleAsync(raft.Id()),
                owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") }),
                owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = currentZone.Text("rowVersion") }));

            var shown = await Show(answers);
            Assert.True(answers.All(a => (int)a.StatusCode < 500), shown);
            var zoneRow = await host.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(z => z.Id == zone.Id()));
            var open = await host.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raft.Id() && c.StatusId == StatusIds.CultivationCycle.Open));
            Assert.False(open && zoneRow.StatusId == StatusIds.Usage.Discontinued, shown);
        }
    }

    // The database answers 100.000 for a stored 100: an edit that resends the plan must not look like a change.
    [SqlServerFact]
    public async Task An_edit_that_resends_the_same_values_at_another_scale_audits_nothing_and_keeps_the_row_version()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id());
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today, 100m, null, 900m));
        var read = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycle.Id()}");

        var edited = await owner.EditCycleAsync(cycle.Id(), new CycleBody(Today, 100.0004m, null, 900.001m, read.Text("rowVersion")));

        Assert.Equal(read.Text("rowVersion"), edited.Text("rowVersion"));
        Assert.False(await host.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordId == cycle.Id() && e.Action == "CultivationCycle.Updated")));
    }

    // The queries W3, C3, LABOR and H1a rely on. A same-day cycle, the gap between two cycles, an edit and a cancel.
    [SqlServerFact]
    public async Task The_cycle_query_on_awkward_data()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var (raft, other) = ((await owner.CreateRaftAsync(zoneId)).Id(), (await owner.CreateRaftAsync(zoneId)).Id());
        var longOne = new CultivationCycle { RaftId = raft, StartDate = Today.AddDays(-30), CloseDate = Today.AddDays(-20), StatusId = StatusIds.CultivationCycle.Closed };
        var sameDay = new CultivationCycle { RaftId = raft, StartDate = Today.AddDays(-19), CloseDate = Today.AddDays(-19), StatusId = StatusIds.CultivationCycle.Closed };
        await host.UseDbAsync(db => db.CultivationCycles.AddRange(longOne, sameDay));
        var open = await owner.StartCycleAsync(raft, new CycleBody(Today.AddDays(-5)));

        using var scope = host.Api.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<ICultivationCycleQuery>();
        Task<Guid?> OnAsync(int days) => query.CycleOnDateAsync(raft, Today.AddDays(days), default).ContinueWith(t => t.Result?.Id);

        Assert.Equal(longOne.Id, await OnAsync(-20));
        Assert.Equal(sameDay.Id, await OnAsync(-19));
        Assert.Null(await OnAsync(-18));
        Assert.Null(await OnAsync(-6));
        Assert.Equal(open.Id(), await OnAsync(-5));
        Assert.Equal(open.Id(), await OnAsync(0));
        Assert.Equal(open.Id(), await OnAsync(30)); // an Open cycle runs on: LABOR plans work days ahead
        Assert.Null(await query.CycleOnDateAsync(raft, DateOnly.MinValue, default));
        Assert.Null(await query.CycleOnDateAsync(other, Today, default));

        var span = (await query.OpenCycleAsync(raft, default))!;
        Assert.Equal((open.Id(), raft, Today.AddDays(-5), (DateOnly?)null), (span.Id, span.RaftId, span.StartDate, span.CloseDate));
        Assert.True(span.IsOpenOn(Today) && !span.IsOpenOn(Today.AddDays(-6)));
        Assert.Equal(sameDay.CloseDate, (await query.CycleOnDateAsync(raft, Today.AddDays(-19), default))!.CloseDate);

        Assert.Equal(Today.AddDays(-5), (await query.EarliestOpenStartByZoneAsync(default))[zoneId]);
        var edited = await owner.EditCycleAsync(open.Id(), new CycleBody(Today.AddDays(-2), RowVersion: open.Text("rowVersion")));
        Assert.Equal(Today.AddDays(-2), (await query.EarliestOpenStartByZoneAsync(default))[zoneId]);

        await (await owner.CancelCycleAsync(open.Id(), edited.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);
        Assert.Null(await query.OpenCycleAsync(raft, default));
        Assert.False((await query.EarliestOpenStartByZoneAsync(default)).ContainsKey(zoneId));
    }

    [SqlServerFact]
    public async Task A_start_in_the_far_past_and_a_later_edit_run_on_sql_server()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id());
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(DateOnly.MinValue));

        Assert.False((await owner.ProgressAsync(raft.Id(), "?date=0001-01-01")).IsNull("cycle"));
        Assert.False((await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today")).GetProperty("lanes").GetArrayLength() == 0);
        Assert.Equal(Today, (await owner.EditCycleAsync(cycle.Id(), new CycleBody(Today, RowVersion: cycle.Text("rowVersion")))).Date("startDate"));
    }
}
