using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>FARM-05 zone–role grants and ACS-10 roles, through the Owner's API.</summary>
public class GrantAndRoleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Water = PermissionFunctions.WaterMeasurements;

    [Fact]
    public async Task Grant_in_zone_A_opens_zone_A_only_and_revoke_closes_it_at_once()
    {
        var owner = await OwnerAsync();
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var roleId = await CreateRoleAsync(owner, (Water, ["View"]));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var grant = await (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zoneA.Id, roleId })).Content.ReadFromJsonAsync<JsonElement>();
        var client = await factory.LoginAsync(staff.Username);
        var a = await client.GetAsync($"/test-only/zones/{zoneA.Id}/water-measurements");
        var b = await client.GetAsync($"/test-only/zones/{zoneB.Id}/water-measurements");

        var revoked = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{grant.GetProperty("id").GetGuid()}/revoke", new { reason = "Moved" });
        var afterRevoke = await client.GetAsync($"/test-only/zones/{zoneA.Id}/water-measurements");
        var history = await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{staff.Id}/grants");

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, b.StatusCode);
        Assert.Equal("Revoked", (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, afterRevoke.StatusCode);
        Assert.Equal("Moved", history[0].GetProperty("revokeReason").GetString());
        // The last effective grant is gone, so the staff member can no longer log in (MSG13).
        Assert.Equal("MSG-ACCOUNT-07", await (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password })).ErrorCodeAsync());
    }

    [Fact]
    public async Task Overlapping_duplicate_grant_and_grants_to_non_staff_are_refused()
    {
        var owner = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var roleId = await CreateRoleAsync(owner, (Water, ["View"]));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, roleId });

        var duplicate = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, roleId });
        var toTrader = await owner.PostAsJsonAsync($"/api/accounts/{trader.Id}/grants", new { zoneId = zone.Id, roleId });
        var unknownZone = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = Guid.NewGuid(), roleId });
        var backwards = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = (Guid?)null, roleId, effectiveFrom = "2026-12-01", effectiveTo = "2026-11-01" });

        Assert.Equal("MSG-GEN-02", await duplicate.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await toTrader.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await unknownZone.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await backwards.ErrorCodeAsync());
    }

    [Fact]
    public async Task Permission_preview_equals_the_enforced_permissions()
    {
        var owner = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var roleId = await CreateRoleAsync(owner, (Water, ["View", "Create"]), (PermissionFunctions.SupplierPayments, ["View"]));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var preview = await (await owner.PostAsJsonAsync("/api/accounts/permission-preview", new { grants = new[] { new { zoneId = zone.Id, roleId } } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, roleId });
        var enforced = (await (await factory.LoginAsync(staff.Username)).GetFromJsonAsync<JsonElement>("/api/me/permissions")).GetProperty("functions");

        Assert.Equal(Normalize(enforced), Normalize(preview));
        // A zone grant does not open the cooperative-wide supplier payments.
        Assert.DoesNotContain(PermissionFunctions.SupplierPayments, preview.EnumerateArray().Select(f => f.GetProperty("function").GetString()));
    }

    [Fact]
    public async Task Role_matrix_needs_view_and_supported_actions_only()
    {
        var owner = await OwnerAsync();

        var withoutView = await owner.PostAsJsonAsync("/api/roles", Role($"R {NewCode()}", (Water, ["Create"])));
        var unsupported = await owner.PostAsJsonAsync("/api/roles", Role($"R {NewCode()}", (PermissionFunctions.DashboardWaterAlerts, ["View", "Edit"])));
        var unknown = await owner.PostAsJsonAsync("/api/roles", Role($"R {NewCode()}", ("ACCOUNT_ACCESS", ["View"])));
        var name = $"R {NewCode()}";
        await CreateRoleAsync(owner, name, (Water, ["View"]));
        var duplicate = await owner.PostAsJsonAsync("/api/roles", Role(name, (Water, ["View"])));
        var catalog = await owner.GetFromJsonAsync<JsonElement>("/api/permission-functions");

        Assert.Equal("MSG-GEN-01", await withoutView.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await unsupported.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await unknown.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-02", await duplicate.ErrorCodeAsync());
        Assert.Equal(PermissionFunctions.All.Count, catalog.GetArrayLength());
    }

    [Fact]
    public async Task Role_change_applies_at_once_and_a_held_role_cannot_be_discontinued()
    {
        var owner = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var name = $"R {NewCode()}";
        var roleId = await CreateRoleAsync(owner, name, (Water, ["View"]));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zone.Id, roleId });
        var client = await factory.LoginAsync(staff.Username);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/test-only/zones/{zone.Id}/water-measurements", null)).StatusCode);

        var role = await owner.GetFromJsonAsync<JsonElement>($"/api/roles/{roleId}");
        var impact = await owner.GetFromJsonAsync<JsonElement>($"/api/roles/{roleId}/impact");
        var updated = await owner.PutAsJsonAsync($"/api/roles/{roleId}", Role(name, role.GetProperty("rowVersion").GetString(), (Water, ["View", "Create"])));
        var discontinue = await owner.PostAsync($"/api/roles/{roleId}/discontinue", null);

        Assert.Equal(1, impact.GetProperty("accountCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/test-only/zones/{zone.Id}/water-measurements", null)).StatusCode);
        Assert.Equal("MSG-GEN-06", await discontinue.ErrorCodeAsync());
    }

    [Fact]
    public async Task Copy_prefills_the_matrix_and_discontinue_reactivate_round_trip()
    {
        var owner = await OwnerAsync();
        var sourceId = await CreateRoleAsync(owner, (Water, ["View", "Export"]));

        var copy = await (await owner.PostAsJsonAsync($"/api/roles/{sourceId}/copy", new { name = $"Copy {NewCode()}" })).Content.ReadFromJsonAsync<JsonElement>();
        var copyId = copy.GetProperty("id").GetGuid();
        var discontinued = await owner.PostAsync($"/api/roles/{copyId}/discontinue", null);
        var twice = await owner.PostAsync($"/api/roles/{copyId}/discontinue", null);
        var reactivated = await owner.PostAsync($"/api/roles/{copyId}/reactivate", null);

        var actions = copy.GetProperty("permissions")[0].GetProperty("actions").EnumerateArray().Select(a => a.GetString());
        Assert.Equal(["View", "Export"], actions);
        Assert.Equal("Discontinued", (await discontinued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("MSG-GEN-09", await twice.ErrorCodeAsync());
        Assert.Equal("In Use", (await reactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    private async Task<HttpClient> OwnerAsync() => await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);

    private static Task<Guid> CreateRoleAsync(HttpClient owner, params (string Function, string[] Actions)[] permissions) =>
        CreateRoleAsync(owner, $"Role {NewCode()}", permissions);

    private static async Task<Guid> CreateRoleAsync(HttpClient owner, string name, params (string Function, string[] Actions)[] permissions)
    {
        var response = await owner.PostAsJsonAsync("/api/roles", Role(name, permissions));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static object Role(string name, params (string Function, string[] Actions)[] permissions) => Role(name, null, permissions);

    private static object Role(string name, string? rowVersion, params (string Function, string[] Actions)[] permissions) => new
    {
        name,
        rowVersion,
        permissions = permissions.Select(p => new { functionCode = p.Function, actions = p.Actions }),
    };

    private static List<string> Normalize(JsonElement functions) => functions.EnumerateArray()
        .Select(f => $"{f.GetProperty("function").GetString()}|{string.Join(",", f.GetProperty("actions").EnumerateArray().Select(a => a.GetString()))}|{f.GetProperty("allZones").GetBoolean()}|{string.Join(",", f.GetProperty("zoneIds").EnumerateArray().Select(z => z.GetString()))}")
        .Order()
        .ToList();
}
