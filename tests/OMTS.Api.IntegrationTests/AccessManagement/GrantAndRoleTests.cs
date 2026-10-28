using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>FARM-05 zone–role pairs of the fixed roles and the read-only roles API (BR-FARM-07/08), through the Owner's API.</summary>
public class GrantAndRoleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Water = PermissionFunctions.WaterMeasurements;

    [Fact]
    public async Task Grant_in_zone_A_opens_zone_A_only_and_revoke_closes_it_at_once_keeping_the_pair_with_its_dates()
    {
        var (owner, ownerAccount) = await OwnerAsync();
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var granted = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zoneA.Id, role = FixedRoles.ZoneManagerCode });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        var grant = await granted.Content.ReadFromJsonAsync<JsonElement>();
        var client = await factory.LoginAsync(staff.Username);
        var a = await client.GetAsync($"/test-only/zones/{zoneA.Id}/water-measurements");
        var b = await client.GetAsync($"/test-only/zones/{zoneB.Id}/water-measurements");

        var revoked = await (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{grant.GetProperty("id").GetGuid()}/revoke", new { reason = "Moved" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var afterRevoke = await client.GetAsync($"/test-only/zones/{zoneA.Id}/water-measurements");
        var history = await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{staff.Id}/grants");

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, b.StatusCode);
        Assert.Equal((FixedRoles.ZoneManagerCode, "Zone Manager", "Effective"), (grant.Text("role"), grant.Text("roleName"), grant.Text("state")));
        Assert.Equal((ownerAccount.Id, ownerAccount.DisplayName), (grant.GetProperty("grantedByAccountId").GetGuid(), grant.Text("grantedByName")));
        Assert.True(grant.GetProperty("grantedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(HttpStatusCode.Forbidden, afterRevoke.StatusCode);
        var row = Assert.Single(history.EnumerateArray());
        Assert.Equal(("Revoked", "Moved", ownerAccount.DisplayName), (row.Text("state"), row.Text("revokeReason"), row.Text("revokedByName")));
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("revokedAt").ValueKind);
        Assert.Equal("Revoked", revoked.Text("state"));
        Assert.False(row.TryGetProperty("effectiveFrom", out _));
        // The last Effective pair is gone, so the staff member can no longer log in (ACCOUNT-02, AC-01).
        Assert.Equal("MSG-ACCOUNT-07", await (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password })).ErrorCodeAsync());
    }

    // FARM-05.0.E1, BR-FARM-08 (A): a pair for one zone duplicates a pair of the same role in that zone or for All zones.
    [Fact]
    public async Task Pair_already_held_in_the_zone_or_for_all_zones_is_refused_with_the_existing_pair()
    {
        var (owner, _) = await OwnerAsync();
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var allZones = await GrantAsync(owner, staff, null, FixedRoles.AccountantCode);
        var zoneManagerA = await GrantAsync(owner, staff, zoneA, FixedRoles.ZoneManagerCode);

        var underAllZones = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zoneB.Id, role = FixedRoles.AccountantCode });
        var allZonesAgain = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = (Guid?)null, role = FixedRoles.AccountantCode });
        var sameZone = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zoneA.Id, role = FixedRoles.ZoneManagerCode });
        // Another role in the same zone, and the same role in another zone, are new pairs.
        var otherZone = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zoneB.Id, role = FixedRoles.ZoneManagerCode });

        foreach (var (response, existing) in new[] { (underAllZones, allZones), (allZonesAgain, allZones), (sameZone, zoneManagerA) })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("MSG-GEN-02", await response.ErrorCodeAsync());
            Assert.Equal(existing, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingId").GetGuid());
        }

        Assert.Equal(HttpStatusCode.Created, otherZone.StatusCode);

        // Re-granting a revoked pair creates a new pair (BR-FARM-08).
        await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{zoneManagerA}/revoke", new { reason = "Season over" });
        var again = await GrantAsync(owner, staff, zoneA, FixedRoles.ZoneManagerCode);
        Assert.NotEqual(zoneManagerA, again);
    }

    [Fact]
    public async Task Locked_account_discontinued_zone_unknown_role_and_non_staff_accounts_are_refused()
    {
        var (owner, _) = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var closedZone = await factory.AddZoneAsync(NewCode());
        await factory.UpdateAsync<FarmingZone>(closedZone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var locked = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.StatusId = StatusIds.Account.Locked);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var accountantRoleId = (await factory.RoleAsync(StaffRole.Accountant)).Id;

        var toLocked = await owner.PostAsJsonAsync($"/api/accounts/{locked.Id}/grants", new { zoneId = zone.Id, role = FixedRoles.ZoneManagerCode });
        var toClosedZone = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = closedZone.Id, role = FixedRoles.ZoneManagerCode });
        var toTrader = await owner.PostAsJsonAsync($"/api/accounts/{trader.Id}/grants", new { zoneId = zone.Id, role = FixedRoles.ZoneManagerCode });
        var unknownZone = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = Guid.NewGuid(), role = FixedRoles.ZoneManagerCode });
        var ownerRole = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, role = "COOPERATIVE_OWNER" });
        var unknownRoleId = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, roleId = Guid.NewGuid() });
        var noRole = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id });
        var mismatch = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, roleId = accountantRoleId, role = FixedRoles.ZoneManagerCode });

        Assert.Equal(HttpStatusCode.Conflict, toLocked.StatusCode);
        Assert.Equal("MSG-GEN-09", await toLocked.ErrorCodeAsync());
        Assert.Equal("Account is Locked and cannot be used for this action.", (await toLocked.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        Assert.Equal("MSG-GEN-09", await toClosedZone.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await toTrader.ErrorCodeAsync());
        Assert.Equal("Zone", (await unknownZone.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        foreach (var response in new[] { ownerRole, unknownRoleId, noRole, mismatch })
        {
            Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
            Assert.Equal("Role", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        Assert.Empty(await factory.QueryAsync(db => Task.FromResult(db.AccessGrants.Where(g => g.AccountId == staff.Id || g.AccountId == locked.Id).ToList())));
    }

    [Fact]
    public async Task Role_id_still_works_and_old_validity_dates_are_ignored()
    {
        var (owner, _) = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var roleId = (await factory.RoleAsync(StaffRole.ZoneManager)).Id;

        var byId = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants",
            new { zoneId = zone.Id, roleId, role = "zone_manager", effectiveFrom = "2027-01-01", effectiveTo = "2026-01-01" });
        var pair = await byId.Content.ReadFromJsonAsync<JsonElement>();
        var revokedTwice = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pair.GetProperty("id").GetGuid()}/revoke", new { reason = "x" });
        revokedTwice = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pair.GetProperty("id").GetGuid()}/revoke", new { reason = "x" });

        Assert.Equal(HttpStatusCode.Created, byId.StatusCode);
        Assert.Equal(("Effective", FixedRoles.ZoneManagerCode, roleId), (pair.Text("state"), pair.Text("role"), pair.GetProperty("roleId").GetGuid()));
        Assert.Equal("MSG-GEN-09", await revokedTwice.ErrorCodeAsync());
    }

    // The preview runs the same rules as the checks: for an Accountant pair in one zone, the functions on records with
    // no zone are open everywhere (BR-FARM-08), the others only in that zone.
    [Fact]
    public async Task Permission_preview_equals_the_enforced_permissions()
    {
        var (owner, _) = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var draft = new { zoneId = zone.Id, role = FixedRoles.AccountantCode };

        var preview = await (await owner.PostAsJsonAsync("/api/accounts/permission-preview", new { grants = new[] { draft } })).Content.ReadFromJsonAsync<JsonElement>();
        await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", draft);
        var enforced = (await (await factory.LoginAsync(staff.Username)).GetFromJsonAsync<JsonElement>("/api/me/permissions")).GetProperty("functions");

        Assert.Equal(Normalize(enforced), Normalize(preview));
        var byFunction = preview.EnumerateArray().ToDictionary(f => f.Text("function")!);
        Assert.True(byFunction[PermissionFunctions.SupplierPayments].GetProperty("allZones").GetBoolean());
        Assert.False(byFunction[PermissionFunctions.SalesInvoices].GetProperty("allZones").GetBoolean());
        Assert.DoesNotContain(Water, byFunction.Keys);
        Assert.DoesNotContain(byFunction.Keys, f => f.StartsWith("DASHBOARD_", StringComparison.Ordinal));
    }

    // BR-FARM-07, AC-02: the two fixed roles are listed with their code and the §I.4.4 permissions; nothing writes them.
    [Fact]
    public async Task Roles_are_the_two_fixed_roles_and_read_only()
    {
        var (owner, _) = await OwnerAsync();
        await factory.EnsureFixedRolesAsync();
        var zoneManagerId = (await factory.RoleAsync(StaffRole.ZoneManager)).Id;

        var roles = await owner.GetFromJsonAsync<JsonElement>("/api/roles");
        var one = await owner.GetFromJsonAsync<JsonElement>($"/api/roles/{zoneManagerId}");
        var catalog = await owner.GetFromJsonAsync<JsonElement>("/api/permission-functions");
        var writes = new[]
        {
            await owner.PostAsJsonAsync("/api/roles", new { name = "Harvest team", permissions = Array.Empty<object>() }),
            await owner.PutAsJsonAsync($"/api/roles/{zoneManagerId}", new { name = "Renamed", permissions = Array.Empty<object>() }),
            await owner.PostAsJsonAsync($"/api/roles/{zoneManagerId}/copy", new { name = "Copy" }),
            await owner.PostAsync($"/api/roles/{zoneManagerId}/discontinue", null),
            await owner.PostAsync($"/api/roles/{zoneManagerId}/reactivate", null),
            await owner.GetAsync($"/api/roles/{zoneManagerId}/impact"),
        };

        Assert.Equal([FixedRoles.AccountantCode, FixedRoles.ZoneManagerCode], roles.EnumerateArray().Select(r => r.Text("code")).Order());
        Assert.Equal(FixedRoles.ZoneManagerCode, one.Text("code"));
        var permissions = one.GetProperty("permissions").EnumerateArray().ToDictionary(p => p.Text("functionCode")!, p => p.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ToList());
        Assert.Equal(FixedRoles.ZoneManager.Permissions.Count, permissions.Count);
        Assert.Equal(["View"], permissions[PermissionFunctions.PurchaseRequests]);
        Assert.Equal(["View", "Create", "Export"], permissions[PermissionFunctions.SalesInvoices]);
        Assert.False(one.TryGetProperty("status", out _));
        Assert.All(writes, w => Assert.True(w.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{w.RequestMessage!.Method} {w.RequestMessage.RequestUri}: {(int)w.StatusCode}"));
        Assert.Equal(PermissionFunctions.All.Count, catalog.GetArrayLength());
        Assert.Equal(2, await factory.QueryAsync(db => Task.FromResult(db.Roles.Count())));
    }

    private async Task<(HttpClient Client, Account Account)> OwnerAsync()
    {
        await factory.EnsureFixedRolesAsync();
        var account = await factory.AddAccountAsync(NewCode(), Password, configure: a => a.DisplayName = $"Owner {NewCode()}");
        return (await factory.LoginAsync(account.Username), account);
    }

    private static async Task<Guid> GrantAsync(HttpClient owner, Account staff, FarmingZone? zone, string role)
    {
        var response = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone?.Id, role });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static List<string> Normalize(JsonElement functions) => functions.EnumerateArray()
        .Select(f => $"{f.GetProperty("function").GetString()}|{string.Join(",", f.GetProperty("actions").EnumerateArray().Select(a => a.GetString()))}|{f.GetProperty("allZones").GetBoolean()}|{string.Join(",", f.GetProperty("zoneIds").EnumerateArray().Select(z => z.GetString()))}")
        .Order()
        .ToList();
}

internal static class AccessJson
{
    public static string? Text(this JsonElement element, string property) => element.GetProperty(property).GetString();
}
