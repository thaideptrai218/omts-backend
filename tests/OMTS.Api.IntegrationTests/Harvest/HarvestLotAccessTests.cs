using System.Net;
using System.Net.Http.Headers;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>§I.4.4: HARVEST-01 is Full for the Owner, Full* for a Zone Manager of the zone (BR-FARM-08) and No for the others; HARVEST-03 is the Owner's alone.</summary>
public class HarvestLotAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task AssertRefusedOnEveryRouteAsync(HttpClient client, Guid raftId, Guid lotId)
    {
        foreach (var answer in new[]
        {
            await client.PostHarvestAsync(raftId),
            await client.GetAsync($"/api/rafts/{raftId}/harvest-lots"),
            await client.GetAsync($"/api/harvest-lots/{lotId}"),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task A_zone_manager_of_the_zone_and_of_all_zones_and_the_owner_record_and_read()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var allZones = await factory.StaffAsync(null);

        foreach (var client in new[] { manager.Client, allZones.Client, raft.Owner.Client })
        {
            var lot = await client.HarvestLotAsync(raft.RaftId);
            Assert.Equal(lot.Id(), (await client.GetHarvestLotAsync(lot.Id())).Id());
            Assert.True((await client.HarvestScreenAsync(raft.RaftId)).GetProperty("canRecordHarvest").GetBoolean());
        }
    }

    [Fact]
    public async Task A_manager_of_another_zone_an_accountant_and_a_trader_are_refused_and_nothing_is_saved()
    {
        var raft = await factory.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        var other = await factory.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());
        var accountantAllZones = await factory.StaffAsync(null, StaffRole.Accountant);
        var accountantHere = await factory.StaffAsync(await factory.ZoneEntityAsync(raft.ZoneId), StaffRole.Accountant);

        await AssertRefusedOnEveryRouteAsync(other.Client, raft.RaftId, lot.Id());
        await AssertRefusedOnEveryRouteAsync(accountantAllZones.Client, raft.RaftId, lot.Id());
        await AssertRefusedOnEveryRouteAsync(accountantHere.Client, raft.RaftId, lot.Id());
        await AssertRefusedOnEveryRouteAsync(await factory.TraderAsync(), raft.RaftId, lot.Id());

        Assert.Equal(1, await factory.LotCountAsync(raft.RaftId));
    }

    // GFR-02: the access is checked before the fields, the parts and the state, so a refusal shows nothing.
    [Fact]
    public async Task A_refused_caller_learns_nothing_of_the_raft_or_the_form()
    {
        var raft = await factory.SeededRaftAsync();
        await factory.CloseCycleAsync(raft.CycleId);
        var other = await factory.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());

        Assert.Equal("MSG-GEN-08", await (await other.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Cages = "0", PhotoField = "other", Photos = [Jpeg] })).ErrorCodeAsync());
    }

    [Fact]
    public async Task Anonymous_callers_get_401_on_every_route()
    {
        var raft = await factory.SeededRaftAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;

        foreach (var answer in new[]
        {
            await client.PostHarvestAsync(raft.RaftId),
            await client.GetAsync($"/api/rafts/{raft.RaftId}/harvest-lots"),
            await client.GetAsync($"/api/harvest-lots/{Guid.NewGuid()}"),
            await client.GetAsync("/api/harvest-settings"),
            await client.PutHarvestSettingsAsync(60m, 3, ""),
            await client.GetAsync("/api/harvest-settings/auto-close-preview?days=1"),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        }
    }

    [Fact]
    public async Task Only_the_owner_reads_and_sets_the_harvest_settings()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var allZones = await factory.StaffAsync(null);
        var accountant = await factory.StaffAsync(null, StaffRole.Accountant);
        var current = await raft.Owner.Client.HarvestSettingsAsync();

        foreach (var client in new[] { manager.Client, allZones.Client, accountant.Client, await factory.TraderAsync() })
        {
            foreach (var answer in new[]
            {
                await client.GetAsync("/api/harvest-settings"),
                await client.PutHarvestSettingsAsync(60m, 3, current.Text("rowVersion")),
                await client.GetAsync("/api/harvest-settings/auto-close-preview?days=1"),
            })
            {
                Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
                Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
            }
        }
    }
}
