using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of §I.4.4 on each WATER-03 route: Owner Full, Zone Manager Full*, every other role No; GBR-01 for a Discontinued zone.</summary>
public class WaterHistoryQaTestsAccess(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private const string Overview = "/api/water-measurements/zones";

    [Fact]
    public async Task Every_route_refuses_the_accountant_at_all_zones_the_trader_and_the_administrator_and_asks_a_stranger_to_log_in()
    {
        var z = await api.ZoneAsync();
        var measurement = await z.RecordedAsync(7m, 20m);
        var accountant = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(accountant, StaffRole.Accountant, zone: null);
        var trader = await api.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var administrator = await api.AddAccountAsync(NewCode(), Password, SubjectType.SystemAdministrator);

        foreach (var url in Routes(z, measurement.Id()))
        {
            foreach (var refused in new[] { accountant, trader, administrator })
            {
                var response = await (await api.LoginAsync(refused.Username)).GetAsync(url);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{refused.SubjectType} on {url}: {response.StatusCode}");
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }

            Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(url)).StatusCode);
        }
    }

    // BR-FARM-08: a role counts only in the zones of its pairs. The Accountant pair on the second zone opens nothing there.
    [Fact]
    public async Task A_zone_manager_of_one_zone_and_accountant_of_another_reads_only_the_first()
    {
        var mine = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var measurement = await other.RecordedAsync(7m, 20m);
        var (manager, account) = await api.StaffAsync(mine.Zone);
        await api.GrantAsync(account, StaffRole.Accountant, other.Zone);

        var overview = await manager.GetFromJsonAsync<JsonElement>(Overview);

        Assert.Equal([mine.Zone.Id], overview.EnumerateArray().Select(r => r.GetProperty("zoneId").GetGuid()));
        foreach (var url in Routes(other, measurement.Id()).Where(u => u != Overview))
        {
            var response = await manager.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{url}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task A_revoked_zone_manager_is_refused_on_every_route_from_the_next_request()
    {
        var z = await api.ZoneAsync();
        var measurement = await z.RecordedAsync(7m, 20m);
        var (manager, account) = await api.StaffAsync(z.Zone);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(Overview)).StatusCode);

        await api.UseDbAsync(db =>
        {
            foreach (var grant in db.AccessGrants.Where(g => g.AccountId == account.Id))
            {
                grant.StatusId = StatusIds.AccessGrant.Revoked;
            }
        });

        foreach (var url in Routes(z, measurement.Id()))
        {
            var response = await manager.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{url}: {response.StatusCode}");
        }
    }

    // GBR-01, BR-FARM-08: the pairs of a discontinued zone stay Effective, so their holders can still view its history.
    [Fact]
    public async Task The_zone_manager_and_the_owner_still_read_a_discontinued_zone_on_every_route()
    {
        var z = await api.ZoneAsync();
        var measurement = await z.RecordedAsync(7m, 20m);
        var (manager, _) = await api.StaffAsync(z.Zone);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        foreach (var url in Routes(z, measurement.Id()))
        {
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await z.Owner.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task A_zone_manager_at_all_zones_reads_every_route()
    {
        var z = await api.ZoneAsync();
        var measurement = await z.RecordedAsync(7m, 20m);
        var allZones = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(allZones, StaffRole.ZoneManager, zone: null);
        var client = await api.LoginAsync(allZones.Username);

        foreach (var url in Routes(z, measurement.Id()))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        }
    }

    // A zone that does not exist says nothing to a Zone Manager without it, and 404 to the Owner.
    [Theory]
    [InlineData("")]
    [InlineData("/chart")]
    [InlineData("/export")]
    public async Task A_missing_zone_is_403_for_a_zone_manager_and_404_for_the_owner(string route)
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var url = $"/api/zones/{Guid.NewGuid()}/water-measurements{route}";

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await z.Owner.GetAsync(url)).StatusCode);
    }

    private static string[] Routes(WaterZone z, Guid measurementId) =>
        [Overview, z.Base, $"{z.Base}/chart", $"{z.Base}/export", $"/api/water-measurements/{measurementId}"];
}
