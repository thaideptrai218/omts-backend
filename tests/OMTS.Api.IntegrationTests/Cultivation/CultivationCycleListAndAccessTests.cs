using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>SCR-CYCLE-01, the raft's cycle list (CYCLE-05.1), and §I.4.4 and BR-FARM-08 on every CYCLE route of this PR.</summary>
public class CultivationCycleListAndAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // CYCLE-01 PRE-3: each command is shown only when its precondition holds.
    [Fact]
    public async Task The_zone_list_shows_each_raft_with_its_cycle_and_the_commands_allowed()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        async Task<Guid> RaftAsync() => (await owner.CreateRaftAsync(zoneId)).Id();
        var (none, open, seeded, closedYesterday, closedToday, gone) = (await RaftAsync(), await RaftAsync(), await RaftAsync(), await RaftAsync(), await RaftAsync(), await owner.CreateRaftAsync(zoneId));

        var openCycle = await owner.StartCycleAsync(open);
        var seededCycle = await owner.StartCycleAsync(seeded);
        await factory.UseDbAsync(db => db.SeedingEvents.Add(new SeedingEvent { CycleId = seededCycle.Id(), SeedLotId = Guid.NewGuid(), SeedingDate = Today, RecordedByAccountId = Guid.NewGuid() }));
        await factory.AddCycleAsync(closedYesterday, Today.AddDays(-30), Today.AddDays(-20));
        var lastClosed = await factory.AddCycleAsync(closedYesterday, Today.AddDays(-10), Today.AddDays(-1));
        await factory.AddCycleAsync(closedToday, Today.AddDays(-10), Today);
        await (await owner.PostAsJsonAsync($"/api/rafts/{gone.Id()}/discontinue", new { rowVersion = gone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        async Task<(bool Start, bool Edit, bool Cancel, JsonElement Row)> RowAsync(Guid raftId)
        {
            var row = await owner.ZoneRowAsync(zoneId, raftId);
            return (row.Flag("canStart"), row.Flag("canEdit"), row.Flag("canCancel"), row);
        }

        var noCycle = await RowAsync(none);
        Assert.Equal((true, false, false), (noCycle.Start, noCycle.Edit, noCycle.Cancel));
        Assert.True(noCycle.Row.IsNull("cycle") && noCycle.Row.IsNull("lastCloseDate"));
        Assert.Equal(5, noCycle.Row.GetProperty("laneCount").GetInt32());

        var inOpen = await RowAsync(open);
        Assert.Equal((false, true, true), (inOpen.Start, inOpen.Edit, inOpen.Cancel));
        Assert.Equal(openCycle.Id(), inOpen.Row.GetProperty("cycle").Id());
        Assert.Equal(openCycle.Text("rowVersion"), inOpen.Row.GetProperty("cycle").Text("rowVersion"));

        var withSeeding = await RowAsync(seeded);
        Assert.Equal((false, true, false), (withSeeding.Start, withSeeding.Edit, withSeeding.Cancel));

        var afterYesterday = await RowAsync(closedYesterday);
        Assert.Equal((true, false, false), (afterYesterday.Start, afterYesterday.Edit, afterYesterday.Cancel));
        Assert.Equal(lastClosed.Id, afterYesterday.Row.GetProperty("cycle").Id());
        Assert.Equal(Today.AddDays(-1), afterYesterday.Row.Date("lastCloseDate"));

        var afterToday = await RowAsync(closedToday);
        Assert.Equal((false, false, false), (afterToday.Start, afterToday.Edit, afterToday.Cancel));

        var discontinued = await RowAsync(gone.Id());
        Assert.Equal((false, false, false), (discontinued.Start, discontinued.Edit, discontinued.Cancel));
        Assert.Equal(StatusIds.Usage.Discontinued, discontinued.Row.GetProperty("raftStatusId").GetInt32());
    }

    [Fact]
    public async Task The_zone_list_of_a_zone_that_does_not_exist_answers_404()
    {
        var owner = (await factory.OwnerAsync()).Client;

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/zones/{Guid.NewGuid()}/cultivation-cycles")).StatusCode);
    }

    [Fact]
    public async Task The_cycles_of_a_raft_come_the_newest_first()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id());
        var oldest = await factory.AddCycleAsync(raft.Id(), Today.AddDays(-60), Today.AddDays(-40));
        var open = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-10)));
        var middle = await factory.AddCycleAsync(raft.Id(), Today.AddDays(-30), Today.AddDays(-20));

        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/cultivation-cycles");

        Assert.Equal([open.Id(), middle.Id, oldest.Id], list.EnumerateArray().Select(c => c.Id()));
        Assert.Equal(("Open", "Closed"), (list[0].Text("status"), list[1].Text("status")));
        Assert.Equal(Today.AddDays(-20), list[1].Date("closeDate"));
        Assert.True(list[0].IsNull("closeDate"));
    }

    private async Task<(HttpClient Owner, Guid ZoneId, Guid OtherZoneId, Guid RaftId, JsonElement Cycle)> ArrangeAsync()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var otherZoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        return (owner, zoneId, otherZoneId, raftId, await owner.StartCycleAsync(raftId));
    }

    private static List<Task<HttpResponseMessage>> EveryRoute(HttpClient client, Guid zoneId, Guid raftId, Guid cycleId) =>
    [
        client.GetAsync($"/api/zones/{zoneId}/cultivation-cycles"),
        client.PostCycleAsync(raftId),
        client.GetAsync($"/api/rafts/{raftId}/cultivation-cycles"),
        client.GetAsync($"/api/rafts/{raftId}/progress"),
        client.GetAsync($"/api/cultivation-cycles/{cycleId}"),
        client.PutCycleAsync(cycleId, new CycleBody(Today, RowVersion: "")),
        client.CancelCycleAsync(cycleId, ""),
    ];

    [Fact]
    public async Task Anonymous_gets_401_on_every_route()
    {
        var (_, zoneId, _, raftId, cycle) = await ArrangeAsync();

        var answers = await Task.WhenAll(EveryRoute(factory.CreateClient(), zoneId, raftId, cycle.Id()));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode));
    }

    // §I.4.4: the Accountant has no CYCLE and no raft reads, also with an All zones pair. A Zone Manager of another zone: BR-FARM-08.
    [Theory]
    [InlineData(StaffRole.Accountant, true)]
    [InlineData(StaffRole.Accountant, false)]
    [InlineData(StaffRole.ZoneManager, false)]
    public async Task Staff_without_the_zone_get_msg_gen_08_on_every_route_and_nothing_changes(StaffRole role, bool allZones)
    {
        var (_, zoneId, otherZoneId, raftId, cycle) = await ArrangeAsync();
        var zone = role == StaffRole.Accountant ? await factory.ZoneEntityAsync(zoneId) : await factory.ZoneEntityAsync(otherZoneId);
        var staff = await factory.StaffAsync(allZones ? null : zone, role);

        foreach (var call in EveryRoute(staff.Client, zoneId, raftId, cycle.Id()))
        {
            var answer = await call;
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
        }

        var kept = await factory.QueryAsync(db => db.CultivationCycles.SingleAsync(c => c.Id == cycle.Id()));
        Assert.Equal(cycle.Date("startDate"), kept.StartDate);
        Assert.Single(await factory.QueryAsync(db => db.CultivationCycles.Where(c => c.RaftId == raftId).ToListAsync()));
    }

    [Fact]
    public async Task A_trader_gets_403_on_every_route()
    {
        var (_, zoneId, _, raftId, cycle) = await ArrangeAsync();
        var trader = await factory.TraderAsync();

        var answers = await Task.WhenAll(EveryRoute(trader, zoneId, raftId, cycle.Id()));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Forbidden, a.StatusCode));
    }

    // A Zone Manager of the zone or of All zones does what the Owner does.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_zone_manager_of_the_zone_or_of_all_zones_starts_edits_and_cancels(bool allZones)
    {
        var (_, zoneId, _, _, _) = await ArrangeAsync();
        var owner = (await factory.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var manager = (await factory.StaffAsync(allZones ? null : await factory.ZoneEntityAsync(zoneId))).Client;

        var cycle = await manager.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-1)));
        var edited = await manager.EditCycleAsync(cycle.Id(), new CycleBody(Today, 9m, RowVersion: cycle.Text("rowVersion")));

        Assert.Equal(9m, edited.GetProperty("expectedSeedQuantity").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/zones/{zoneId}/cultivation-cycles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/rafts/{raftId}/progress")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.CancelCycleAsync(cycle.Id(), edited.Text("rowVersion"))).StatusCode);
    }
}
