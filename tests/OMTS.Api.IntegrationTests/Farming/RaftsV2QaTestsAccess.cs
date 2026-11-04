using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Independent QA of §I.4.4 and BR-FARM-08 on every FARM-04 route: Owner, Zone Manager of this and another zone, Accountant, Trader, anonymous.</summary>
public class RaftsV2QaTestsAccess(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, JsonElement ZoneA, JsonElement ZoneB, JsonElement Raft, Guid PhotoId)> ArrangeAsync()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zoneA = await owner.CreateZoneAsync();
        var zoneB = await owner.CreateZoneAsync();
        var raft = await owner.CreateRaftAsync(zoneA.Id());
        var withPhoto = await owner.AddRaftPhotoAsync(raft.Id());
        return (owner, zoneA, zoneB, withPhoto, withPhoto.GetProperty("photos")[0].Id());
    }

    private static List<Task<HttpResponseMessage>> OwnerOnlyCalls(HttpClient client, Guid zoneId, JsonElement raft, Guid photoId)
    {
        var version = raft.Text("rowVersion");
        var raftId = raft.Id();
        return
        [
            client.GetAsync($"/api/zones/{zoneId}/rafts/next-code"),
            client.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", NewRaftBody()),
            client.PutAsJsonAsync($"/api/rafts/{raftId}", NewRaftBody() with { RowVersion = version }),
            client.UploadRaftPhotoAsync(raftId),
            client.DeleteAsync($"/api/rafts/{raftId}/photos/{photoId}"),
            client.PostAsJsonAsync($"/api/rafts/{raftId}/discontinue", new { rowVersion = version }),
            client.PostAsJsonAsync($"/api/rafts/{raftId}/resume", new { rowVersion = version }),
            client.PostAsJsonAsync($"/api/rafts/{raftId}/reactivate", new { rowVersion = version }),
        ];
    }

    private static List<Task<HttpResponseMessage>> ReadCalls(HttpClient client, Guid zoneId, Guid raftId) =>
    [
        client.GetAsync($"/api/zones/{zoneId}/rafts"),
        client.GetAsync($"/api/rafts/{raftId}"),
        client.GetAsync($"/api/rafts/{raftId}/lanes/today"),
    ];

    [Fact]
    public async Task Anonymous_gets_401_on_every_route_including_the_map()
    {
        var (_, zoneA, _, raft, photoId) = await ArrangeAsync();
        var anonymous = factory.CreateClient();

        var answers = await Task.WhenAll(
            OwnerOnlyCalls(anonymous, zoneA.Id(), raft, photoId).Concat(ReadCalls(anonymous, zoneA.Id(), raft.Id())).Append(anonymous.GetAsync("/api/zones/map")));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode));
    }

    [Theory]
    [InlineData(StaffRole.ZoneManager)]
    [InlineData(StaffRole.Accountant)]
    public async Task Staff_of_the_zone_get_msg_gen_08_on_every_owner_route_and_nothing_changes(StaffRole role)
    {
        var (_, zoneA, _, raft, photoId) = await ArrangeAsync();
        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneA.Id()), role);

        foreach (var call in OwnerOnlyCalls(staff.Client, zoneA.Id(), raft, photoId))
        {
            var answer = await call;
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
        }

        Assert.Single(await factory.RaftPhotoKeysAsync(raft.Id()));
    }

    [Fact]
    public async Task All_zones_staff_and_traders_get_msg_gen_08_on_every_owner_route()
    {
        var (_, zoneA, _, raft, photoId) = await ArrangeAsync();
        foreach (var client in new[] { (await factory.StaffAsync(null, StaffRole.ZoneManager)).Client, (await factory.StaffAsync(null, StaffRole.Accountant)).Client, await factory.TraderAsync() })
        {
            foreach (var call in OwnerOnlyCalls(client, zoneA.Id(), raft, photoId))
            {
                Assert.Equal("MSG-GEN-08", await (await call).ErrorCodeAsync());
            }
        }
    }

    [Fact]
    public async Task Owner_reads_every_route_and_a_zone_manager_only_the_zones_of_the_pair()
    {
        var (owner, zoneA, zoneB, raft, _) = await ArrangeAsync();
        foreach (var call in ReadCalls(owner, zoneA.Id(), raft.Id()))
        {
            await (await call).ReadAsync(HttpStatusCode.OK);
        }

        var here = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneA.Id()));
        foreach (var call in ReadCalls(here.Client, zoneA.Id(), raft.Id()))
        {
            await (await call).ReadAsync(HttpStatusCode.OK);
        }

        var elsewhere = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneB.Id()));
        foreach (var call in ReadCalls(elsewhere.Client, zoneA.Id(), raft.Id()))
        {
            Assert.Equal("MSG-GEN-08", await (await call).ErrorCodeAsync());
        }

        var map = (await elsewhere.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().ToList();
        Assert.Equal([zoneB.Id()], map.Select(z => z.Id()));
        Assert.Equal(0, map[0].GetProperty("rafts").GetArrayLength());
    }

    // A trader or a revoked account sees no zone and no raft on the map; GFR-02: a revoke applies from the next action.
    [Fact]
    public async Task A_trader_sees_nothing_and_a_revoked_pair_closes_the_reads_at_the_next_action()
    {
        var (_, zoneA, _, raft, _) = await ArrangeAsync();
        var trader = await factory.TraderAsync();
        Assert.Equal(0, (await trader.GetFromJsonAsync<JsonElement>("/api/zones/map")).GetArrayLength());
        foreach (var call in ReadCalls(trader, zoneA.Id(), raft.Id()))
        {
            Assert.Equal("MSG-GEN-08", await (await call).ErrorCodeAsync());
        }

        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneA.Id()));
        await (await staff.Client.GetAsync($"/api/rafts/{raft.Id()}/lanes/today")).ReadAsync(HttpStatusCode.OK);

        await factory.UpdateAsync<AccessGrant>(staff.Grant!.Id, g =>
        {
            g.StatusId = StatusIds.AccessGrant.Revoked;
            g.RevokedAt = DateTimeOffset.UtcNow;
        });

        var refused = await staff.Client.GetAsync($"/api/rafts/{raft.Id()}/lanes/today");
        Assert.Contains(refused.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized });
    }

    // §I.4.4: FARM-04 and CYCLE-05 are "No" for the Accountant, whose scope lists no raft or lane right; decision Q27 keeps the raft reads for the
    // Zone Manager only (CYCLE, HARVEST, WATER screens). The Accountant still sees the zone, on the map too, without its rafts.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Accountant_is_refused_the_raft_reads_of_the_matrix(bool allZones)
    {
        var (_, zoneA, _, raft, _) = await ArrangeAsync();
        var accountant = await factory.StaffAsync(allZones ? null : await factory.ZoneEntityAsync(zoneA.Id()), StaffRole.Accountant);

        foreach (var call in ReadCalls(accountant.Client, zoneA.Id(), raft.Id()))
        {
            var answer = await call;
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
        }

        await (await accountant.Client.GetAsync($"/api/zones/{zoneA.Id()}")).ReadAsync(HttpStatusCode.OK);
        var onMap = (await accountant.Client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().Single(z => z.Id() == zoneA.Id());
        Assert.Equal(0, onMap.GetProperty("rafts").GetArrayLength());
    }

    // BR-FARM-08, per pair: one person, Zone Manager in zone A and Accountant in zone B, reads the rafts of A and not those of B.
    [Fact]
    public async Task A_person_with_both_roles_reads_only_the_rafts_of_the_zone_manager_pair()
    {
        var (owner, zoneA, zoneB, raftA, _) = await ArrangeAsync();
        var raftB = await owner.CreateRaftAsync(zoneB.Id());
        var (staff, _) = await factory.StaffAccountAsync(await factory.ZoneEntityAsync(zoneA.Id()));
        await factory.GrantAsync(staff, StaffRole.Accountant, await factory.ZoneEntityAsync(zoneB.Id()));
        var client = await factory.LoginAsync(staff.Username);

        foreach (var call in ReadCalls(client, zoneA.Id(), raftA.Id()))
        {
            await (await call).ReadAsync(HttpStatusCode.OK);
        }

        foreach (var call in ReadCalls(client, zoneB.Id(), raftB.Id()))
        {
            Assert.Equal("MSG-GEN-08", await (await call).ErrorCodeAsync());
        }

        var map = (await client.GetFromJsonAsync<JsonElement>("/api/zones/map")).EnumerateArray().ToDictionary(z => z.Id(), z => z.GetProperty("rafts").GetArrayLength());
        Assert.Equal(1, map[zoneA.Id()]);
        Assert.Equal(0, map[zoneB.Id()]);
    }
}
