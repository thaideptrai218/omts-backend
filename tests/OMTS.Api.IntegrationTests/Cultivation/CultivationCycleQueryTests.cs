using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary><see cref="ICultivationCycleQuery"/> and the cycle reads as SQL Server translates them.</summary>
public class CultivationCycleQueryTests
{
    // The close day counts, a Closed cycle is not the Open one, and zones without an Open cycle are left out.
    [SqlServerFact]
    public async Task The_cycle_query_answers_the_open_cycle_the_cycle_on_a_date_and_the_earliest_open_start_per_zone()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var (zoneA, zoneB, zoneC) = ((await owner.CreateZoneAsync()).Id(), (await owner.CreateZoneAsync()).Id(), (await owner.CreateZoneAsync()).Id());
        var (raftA1, raftA2, raftB) = ((await owner.CreateRaftAsync(zoneA)).Id(), (await owner.CreateRaftAsync(zoneA)).Id(), (await owner.CreateRaftAsync(zoneB)).Id());
        var raftC = (await owner.CreateRaftAsync(zoneC)).Id();
        var closed = new CultivationCycle { RaftId = raftA1, StartDate = Today.AddDays(-30), CloseDate = Today.AddDays(-20), StatusId = StatusIds.CultivationCycle.Closed };
        var closedOnly = new CultivationCycle { RaftId = raftC, StartDate = Today.AddDays(-90), CloseDate = Today.AddDays(-80), StatusId = StatusIds.CultivationCycle.Closed };
        await host.UseDbAsync(db => db.CultivationCycles.AddRange(closed, closedOnly));
        var open = await owner.StartCycleAsync(raftA1, new CycleBody(Today.AddDays(-5)));
        await owner.StartCycleAsync(raftA2, new CycleBody(Today.AddDays(-7)));
        await owner.StartCycleAsync(raftB, new CycleBody(Today.AddDays(-1)));

        using var scope = host.Api.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<ICultivationCycleQuery>();

        Assert.Equal(open.Id(), (await query.OpenCycleAsync(raftA1, default))!.Id);
        Assert.Null(await query.OpenCycleAsync(raftC, default));

        Assert.Equal(closed.Id, (await query.CycleOnDateAsync(raftA1, Today.AddDays(-30), default))!.Id);
        Assert.Equal(closed.Id, (await query.CycleOnDateAsync(raftA1, Today.AddDays(-20), default))!.Id);
        Assert.Null(await query.CycleOnDateAsync(raftA1, Today.AddDays(-19), default));
        Assert.Null(await query.CycleOnDateAsync(raftA1, Today.AddDays(-31), default));
        Assert.Equal(open.Id(), (await query.CycleOnDateAsync(raftA1, Today, default))!.Id);

        var earliest = await query.EarliestOpenStartByZoneAsync(default);
        Assert.Equal(Today.AddDays(-7), earliest[zoneA]);
        Assert.Equal(Today.AddDays(-1), earliest[zoneB]);
        Assert.False(earliest.ContainsKey(zoneC));
    }

    [SqlServerFact]
    public async Task The_zone_list_the_raft_list_and_the_progress_view_run_on_sql_server()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raft = await owner.CreateRaftAsync(zoneId, NewRaftBody(lanes: 2));
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-1)));
        await owner.CreateRaftAsync(zoneId);

        var row = await owner.ZoneRowAsync(zoneId, raft.Id());
        var view = await owner.ProgressAsync(raft.Id());
        var list = await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/rafts/{raft.Id()}/cultivation-cycles");

        Assert.Equal(cycle.Id(), row.GetProperty("cycle").Id());
        Assert.Equal((cycle.Id(), 2), (view.GetProperty("cycle").Id(), view.GetProperty("lanes").GetArrayLength()));
        Assert.Equal(cycle.Id(), list[0].Id());
    }
}
