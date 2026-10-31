using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Authorization;

public class AuthorizationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "Secret123";

    // NFR-01: a grant in zone A opens zone A only.
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

    [Fact]
    public async Task Action_not_granted_by_the_role_is_denied()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var client = await StaffWithGrantAsync(zone);

        var create = await client.PostAsync($"/test-only/zones/{zone.Id}/water-measurements", null);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    // UC11: an All zones grant applies to every zone and to cooperative-wide data.
    [Fact]
    public async Task All_zones_grant_applies_everywhere()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var client = await StaffWithGrantAsync(zone: null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test-only/supplier-payments")).StatusCode);
    }

    // Section 4.4: cooperative-wide data needs a role assigned at All zones.
    [Fact]
    public async Task Zone_grant_does_not_open_cooperative_wide_data()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var client = await StaffWithGrantAsync(zone);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/test-only/supplier-payments")).StatusCode);
    }

    // BR-19: revoking applies immediately, even though the permission was just cached.
    [Fact]
    public async Task Revoked_grant_is_denied_immediately()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (client, _, grant, _) = await StaffSetupAsync(zone);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);

        await factory.UpdateAsync<AccessGrant>(grant.Id, g =>
        {
            g.StatusId = StatusIds.AccessGrant.Revoked;
            g.RevokedAt = DateTimeOffset.UtcNow;
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
    }

    // BR-19: role permission edits apply immediately to every account holding the role.
    [Fact]
    public async Task Role_permission_change_applies_immediately()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (client, _, _, role) = await StaffSetupAsync(zone);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/test-only/zones/{zone.Id}/water-measurements", null)).StatusCode);

        await factory.UseDbAsync(db =>
        {
            var permission = db.RolePermissions.Single(p => p.RoleId == role.Id && p.FunctionCode == PermissionFunctions.WaterMeasurements);
            permission.CanCreate = true;
        });

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/test-only/zones/{zone.Id}/water-measurements", null)).StatusCode);
    }

    // FARM-05: "Expired" is derived from EffectiveTo; the grant stops applying although its status stays Effective.
    [Fact]
    public async Task Expired_grant_is_denied()
    {
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var (client, account, _, role) = await StaffSetupAsync(zoneA);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await factory.GrantAsync(account, role, zoneB, g =>
        {
            g.EffectiveFrom = yesterday.AddDays(-10);
            g.EffectiveTo = yesterday;
        });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zoneA.Id}/water-measurements")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/test-only/zones/{zoneB.Id}/water-measurements")).StatusCode);
    }

    // ACS-10: a discontinued role no longer gives its permissions, effective on the next request.
    [Fact]
    public async Task Discontinued_role_is_denied_immediately()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (client, _, _, role) = await StaffSetupAsync(zone);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);

        await factory.UpdateAsync<Role>(role.Id, r => r.StatusId = StatusIds.Usage.Discontinued);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
    }

    // FARM-05: permissions are the union of the roles granted in a zone, each limited to its own zone.
    [Fact]
    public async Task Permissions_are_the_union_of_roles_limited_to_their_zones()
    {
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var (client, account, _, _) = await StaffSetupAsync(zoneA);
        var creator = await factory.AddRoleAsync($"Role {NewCode()}", (PermissionFunctions.WaterMeasurements, View: true, Create: true, Edit: false));
        await factory.GrantAsync(account, creator, zoneB);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/test-only/zones/{zoneB.Id}/water-measurements", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/test-only/zones/{zoneA.Id}/water-measurements", null)).StatusCode);

        // A second role in zone A adds its rights there too.
        await factory.GrantAsync(account, creator, zoneA);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/test-only/zones/{zoneA.Id}/water-measurements", null)).StatusCode);
    }

    // UC11 branch 1b: locking invalidates every open session.
    [Fact]
    public async Task Locked_account_is_rejected_with_an_existing_token()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (client, account, _, _) = await StaffSetupAsync(zone);

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
        var (client, account, _, _) = await StaffSetupAsync(zone, a => a.PhoneVerifiedAt = null);

        var blocked = await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("MSG-ACCOUNT-04", await ErrorCodeAsync(blocked));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/test-only/verify-phone", null)).StatusCode);

        // Verifying the number lifts the gate right away (UC08 saves the account).
        await factory.UpdateAsync<Account>(account.Id, a => a.PhoneVerifiedAt = DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);
    }

    [Fact]
    public async Task Me_permissions_lists_functions_per_zone()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var client = await StaffWithGrantAsync(zone);

        var permissions = await client.GetFromJsonAsync<JsonElement>("/api/me/permissions");

        var water = permissions.GetProperty("functions").EnumerateArray()
            .Single(f => f.GetProperty("function").GetString() == PermissionFunctions.WaterMeasurements);
        Assert.False(water.GetProperty("allZones").GetBoolean());
        Assert.Equal(zone.Id, water.GetProperty("zoneIds")[0].GetGuid());
        Assert.Equal(new string?[] { "View" }, water.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(zone.Code, permissions.GetProperty("grants")[0].GetProperty("zoneCode").GetString());
    }

    private async Task<HttpClient> StaffWithGrantAsync(FarmingZone? zone) => (await StaffSetupAsync(zone)).Client;

    /// <summary>A staff account holding a role that may only view water measurements, granted in <paramref name="zone"/> (null = All zones).</summary>
    private async Task<(HttpClient Client, Account Account, AccessGrant Grant, Role Role)> StaffSetupAsync(
        FarmingZone? zone, Action<Account>? configure = null)
    {
        var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, configure);
        var role = await factory.AddRoleAsync(
            $"Role {NewCode()}",
            (PermissionFunctions.WaterMeasurements, View: true, Create: false, Edit: false),
            (PermissionFunctions.SupplierPayments, View: true, Create: false, Edit: false));
        var grant = await factory.GrantAsync(account, role, zone);
        return (await LoginAsync(account.Username), account, grant, role);
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
