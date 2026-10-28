using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Authorization;

/// <summary>GFR-02 with the fixed roles of §I.4.4 (BR-FARM-07/08): zone limits, role-only records and immediate changes.</summary>
public class AuthorizationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "Secret123";

    // NFR-01: a pair in zone A opens zone A only.
    [Fact]
    public async Task Zone_grant_allows_its_zone_and_denies_other_zones()
    {
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var client = await StaffWithGrantAsync(zoneA);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zoneA.Id}/water-measurements")).StatusCode);
        var otherZone = await client.GetAsync($"/test-only/zones/{zoneB.Id}/water-measurements");
        Assert.Equal(HttpStatusCode.Forbidden, otherZone.StatusCode);
        Assert.Equal("MSG-GEN-08", await ErrorCodeAsync(otherZone));
    }

    // §I.4.4 WATER-01…03: the Accountant role has no water rights, even in a zone of its pair.
    [Fact]
    public async Task Function_not_held_by_the_role_is_denied_in_the_zone_of_the_pair()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var client = await StaffWithGrantAsync(zone, StaffRole.Accountant);

        var view = await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements");
        var create = await client.PostAsync($"/test-only/zones/{zone.Id}/water-measurements", null);

        Assert.Equal(HttpStatusCode.Forbidden, view.StatusCode);
        Assert.Equal("MSG-GEN-08", await ErrorCodeAsync(create));
    }

    // FARM-05: an All zones pair applies to every zone, also to zones created later (BR-FARM-08).
    [Fact]
    public async Task All_zones_grant_applies_everywhere()
    {
        var client = await StaffWithGrantAsync(zone: null);
        var zoneCreatedLater = await factory.AddZoneAsync(NewCode());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zoneCreatedLater.Id}/water-measurements")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/test-only/zones/{zoneCreatedLater.Id}/water-measurements", null)).StatusCode);
    }

    // BR-FARM-08, P-6: records with no zone follow the role only, so an Accountant pair for one zone opens supplier
    // payments; a Zone Manager pair, even for All zones, does not (PURCHASE-04/05 No for the Zone Manager).
    [Fact]
    public async Task Records_with_no_zone_follow_the_role_only()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var zoneAccountant = await StaffWithGrantAsync(zone, StaffRole.Accountant);
        var allZonesManager = await StaffWithGrantAsync(null);

        Assert.Equal(HttpStatusCode.OK, (await zoneAccountant.GetAsync("/test-only/supplier-payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await allZonesManager.GetAsync("/test-only/supplier-payments")).StatusCode);
    }

    // GFR-02: revoking applies from the next action, even though the permission was just cached.
    [Fact]
    public async Task Revoked_grant_is_denied_immediately()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (client, _, grant) = await StaffSetupAsync(zone);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);

        await factory.UpdateAsync<AccessGrant>(grant.Id, g =>
        {
            g.StatusId = StatusIds.AccessGrant.Revoked;
            g.RevokedAt = DateTimeOffset.UtcNow;
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
    }

    // GFR-02: a new pair applies from the next action, although the refusal was just cached.
    [Fact]
    public async Task New_grant_applies_immediately()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (client, account, _) = await StaffSetupAsync(zone, role: StaffRole.Accountant);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/test-only/zones/{zone.Id}/water-measurements", null)).StatusCode);

        await factory.GrantAsync(account, StaffRole.ZoneManager, zone);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/test-only/zones/{zone.Id}/water-measurements", null)).StatusCode);
    }

    // FARM-05 POST-2: one account may hold different roles in different zones; each role is limited to its zones.
    [Fact]
    public async Task Permissions_are_the_union_of_roles_limited_to_their_zones()
    {
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var (client, account, _) = await StaffSetupAsync(zoneA, role: StaffRole.Accountant);
        await factory.GrantAsync(account, StaffRole.ZoneManager, zoneB);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/test-only/zones/{zoneB.Id}/water-measurements", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/test-only/zones/{zoneA.Id}/water-measurements", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test-only/supplier-payments")).StatusCode);

        // A second role in zone A adds its rights there too.
        await factory.GrantAsync(account, StaffRole.ZoneManager, zoneA);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/test-only/zones/{zoneA.Id}/water-measurements", null)).StatusCode);
    }

    // ACCOUNT-09.2: locking invalidates every open session.
    [Fact]
    public async Task Locked_account_is_rejected_with_an_existing_token()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (client, account, _) = await StaffSetupAsync(zone);

        await factory.UpdateAsync<Account>(account.Id, a => a.StatusId = StatusIds.Account.Locked);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
    }

    [Fact]
    public async Task Cooperative_owner_is_allowed_everywhere_and_trader_nowhere()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var owner = await LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var trader = await LoginAsync((await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/test-only/supplier-payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
    }

    // UC02 branch 2c: with a temporary password only UC04 endpoints (and logout) are open.
    [Fact]
    public async Task Must_change_password_gate_allows_only_password_change()
    {
        var account = await factory.AddAccountAsync(NewCode(), Password, configure: a =>
        {
            a.MustChangePassword = true;
            a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow;
        });
        var client = await LoginAsync(account.Username);

        var blocked = await client.GetAsync("/test-only/supplier-payments");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", await ErrorCodeAsync(blocked));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me/permissions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/test-only/change-password", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    // BR-34, MSG67: traders and staff without a verified phone only reach phone verification.
    [Fact]
    public async Task Unverified_phone_gate_allows_only_phone_verification()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (client, account, _) = await StaffSetupAsync(zone, a => a.PhoneVerifiedAt = null);

        var blocked = await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("MSG-ACCOUNT-04", await ErrorCodeAsync(blocked));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/test-only/verify-phone", null)).StatusCode);

        // Verifying the number lifts the gate right away (UC08 saves the account).
        await factory.UpdateAsync<Account>(account.Id, a => a.PhoneVerifiedAt = DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
    }

    [Fact]
    public async Task Me_permissions_lists_the_fixed_role_functions_per_zone()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var client = await StaffWithGrantAsync(zone);

        var permissions = await client.GetFromJsonAsync<JsonElement>("/api/me/permissions");

        var functions = permissions.GetProperty("functions").EnumerateArray().ToDictionary(f => f.GetProperty("function").GetString()!);
        var water = functions[PermissionFunctions.WaterMeasurements];
        Assert.False(water.GetProperty("allZones").GetBoolean());
        Assert.Equal(zone.Id, water.GetProperty("zoneIds")[0].GetGuid());
        Assert.Equal(new string?[] { "View", "Create", "Edit", "Export" }, water.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        // Suppliers have no zone: the role opens them everywhere (BR-FARM-08). Dashboard tiles are Owner-only (FARM-06).
        Assert.True(functions[PermissionFunctions.Suppliers].GetProperty("allZones").GetBoolean());
        Assert.DoesNotContain(functions.Keys, f => f.StartsWith("DASHBOARD_", StringComparison.Ordinal));
        var grant = permissions.GetProperty("grants")[0];
        Assert.Equal(zone.Code, grant.GetProperty("zoneCode").GetString());
        Assert.Equal(FixedRoles.ZoneManagerCode, grant.GetProperty("roleCode").GetString());
    }

    private async Task<HttpClient> StaffWithGrantAsync(FarmingZone? zone, StaffRole role = StaffRole.ZoneManager) =>
        (await StaffSetupAsync(zone, role: role)).Client;

    /// <summary>A staff account holding one pair of the fixed <paramref name="role"/> in <paramref name="zone"/> (null = All zones).</summary>
    private async Task<(HttpClient Client, Account Account, AccessGrant Grant)> StaffSetupAsync(
        FarmingZone? zone, Action<Account>? configure = null, StaffRole role = StaffRole.ZoneManager)
    {
        var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, configure);
        var grant = await factory.GrantAsync(account, role, zone);
        return (await LoginAsync(account.Username), account, grant);
    }

    private async Task<HttpClient> LoginAsync(string username)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier = username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private static string NewCode() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
}
