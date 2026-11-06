using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of §I.4.4 and BR-FARM-08 on every route of the PR, lanes of today included, and for a person with both roles.</summary>
public class CyclesQaTestsAccess(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, Guid ZoneId, Guid RaftId, JsonElement Cycle)> InZoneAsync()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        return (owner, zoneId, raftId, await owner.StartCycleAsync(raftId));
    }

    private static IEnumerable<Func<Task<HttpResponseMessage>>> EveryRoute(HttpClient client, Guid zoneId, Guid raftId, Guid cycleId) =>
    [
        () => client.GetAsync($"/api/zones/{zoneId}/cultivation-cycles"),
        () => client.PostCycleAsync(raftId),
        () => client.GetAsync($"/api/rafts/{raftId}/cultivation-cycles"),
        () => client.GetAsync($"/api/rafts/{raftId}/progress"),
        () => client.GetAsync($"/api/rafts/{raftId}/progress?cycleId={cycleId}"),
        () => client.GetAsync($"/api/rafts/{raftId}/lanes/today"),
        () => client.GetAsync($"/api/cultivation-cycles/{cycleId}"),
        () => client.PutCycleAsync(cycleId, new CycleBody(Today, RowVersion: "")),
        () => client.CancelCycleAsync(cycleId, ""),
    ];

    private async Task<HttpClient> StaffWithPairsAsync(params (StaffRole Role, Guid? ZoneId)[] pairs)
    {
        var password = NewPassword();
        var staff = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        foreach (var (role, zoneId) in pairs)
        {
            await factory.GrantAsync(staff, role, zoneId is { } id ? await factory.ZoneEntityAsync(id) : null);
        }

        return await factory.LoginAsync(staff.Username, password);
    }

    private async Task AssertRefusedAsync(HttpClient client, Guid zoneId, Guid raftId, Guid cycleId)
    {
        foreach (var call in EveryRoute(client, zoneId, raftId, cycleId))
        {
            var answer = await call();
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
        }

        Assert.Single(await factory.QueryAsync(db => db.CultivationCycles.Where(c => c.RaftId == raftId).ToListAsync()));
    }

    [Fact]
    public async Task Anonymous_gets_401_on_every_route_and_the_lanes_of_today()
    {
        var (_, zoneId, raftId, cycle) = await InZoneAsync();
        var anonymous = factory.CreateClient();

        foreach (var call in EveryRoute(anonymous, zoneId, raftId, cycle.Id()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await call()).StatusCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_accountant_is_refused_everywhere_also_on_the_lanes_of_today(bool allZones)
    {
        var (_, zoneId, raftId, cycle) = await InZoneAsync();

        var accountant = await StaffWithPairsAsync((StaffRole.Accountant, allZones ? null : zoneId));

        await AssertRefusedAsync(accountant, zoneId, raftId, cycle.Id());
    }

    [Fact]
    public async Task A_zone_manager_of_another_zone_is_refused_everywhere()
    {
        var (owner, zoneId, raftId, cycle) = await InZoneAsync();

        var manager = await StaffWithPairsAsync((StaffRole.ZoneManager, (await owner.CreateZoneAsync()).Id()));

        await AssertRefusedAsync(manager, zoneId, raftId, cycle.Id());
    }

    // BR-FARM-08: each role is limited to the zones of its pairs. An All zones Accountant pair opens no raft of zone B to a Zone Manager of zone A.
    [Fact]
    public async Task A_zone_manager_of_zone_a_who_is_accountant_of_all_zones_works_in_a_and_is_refused_in_b()
    {
        var (owner, zoneA, raftA, _) = await InZoneAsync();
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var cycleB = await owner.StartCycleAsync(raftB);

        var both = await StaffWithPairsAsync((StaffRole.ZoneManager, zoneA), (StaffRole.Accountant, null));

        await AssertRefusedAsync(both, zoneB, raftB, cycleB.Id());
        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync($"/api/zones/{zoneA}/cultivation-cycles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync($"/api/rafts/{raftA}/lanes/today")).StatusCode);
        var freshRaft = (await owner.CreateRaftAsync(zoneA)).Id();
        var started = await both.StartCycleAsync(freshRaft);
        var edited = await both.EditCycleAsync(started.Id(), new CycleBody(Today, 1m, RowVersion: started.Text("rowVersion")));
        Assert.Equal(HttpStatusCode.NoContent, (await both.CancelCycleAsync(started.Id(), edited.Text("rowVersion"))).StatusCode);
    }

    [Fact]
    public async Task The_owner_works_on_every_route()
    {
        var (owner, zoneId, raftId, cycle) = await InZoneAsync();

        foreach (var path in new[]
        {
            $"/api/zones/{zoneId}/cultivation-cycles", $"/api/rafts/{raftId}/cultivation-cycles", $"/api/rafts/{raftId}/progress",
            $"/api/rafts/{raftId}/lanes/today", $"/api/cultivation-cycles/{cycle.Id()}",
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(path)).StatusCode);
        }

        var edited = await owner.EditCycleAsync(cycle.Id(), new CycleBody(Today, 2m, RowVersion: cycle.Text("rowVersion")));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(cycle.Id(), edited.Text("rowVersion"))).StatusCode);
    }
}
