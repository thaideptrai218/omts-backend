using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>Acceptance criteria AC-01 and AC-02 of SRS 27/10/2026 (Part V): zone–role pairs of the fixed roles, end to end.</summary>
public class FixedRoleScenarioTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // AC-01: an account sees only the functions of its roles in the zones of its pairs; with Zone A – Zone Manager and
    // Zone C – Accountant it has different permissions in the two zones; with no Effective pair it cannot log in.
    [Fact]
    public async Task Ac01_an_account_with_two_pairs_has_different_permissions_in_the_two_zones()
    {
        await factory.EnsureFixedRolesAsync();
        var owner = await factory.OwnerAsync();
        var zoneA = (await owner.Client.CreateZoneAsync()).Id();
        var zoneC = (await owner.Client.CreateZoneAsync()).Id();

        // ACCOUNT-09 creates the staff account, FARM-05 adds the second pair.
        var (accountId, username, _) = await CreateStaffAsync(owner.Client, zoneA, FixedRoles.ZoneManagerCode);
        var second = await owner.Client.PostAsJsonAsync($"/api/accounts/{accountId}/grants", new { zoneId = zoneC, role = FixedRoles.AccountantCode });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        await UseOwnPasswordAsync(accountId);
        var staff = await factory.LoginAsync(username);

        // Zone A – Zone Manager: water measurements there, not in zone C.
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/zones/{zoneA}/water-measurements")).StatusCode);
        var waterC = await staff.GetAsync($"/api/zones/{zoneC}/water-measurements");
        Assert.Equal("MSG-GEN-08", await waterC.ErrorCodeAsync());
        // Zone C – Accountant: supplier payments and debts have no zone, so they follow the role only (BR-FARM-08).
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/supplier-payments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/suppliers/debts")).StatusCode);
        // Owner-only functions stay closed (FARM-05, ACCOUNT-09).
        Assert.Equal("MSG-GEN-08", await (await staff.GetAsync("/api/accounts")).ErrorCodeAsync());

        var permissions = await staff.GetFromJsonAsync<JsonElement>("/api/me/permissions");
        var functions = permissions.GetProperty("functions").EnumerateArray().ToDictionary(f => f.Text("function")!);
        Assert.Equal([zoneA], functions[PermissionFunctions.WaterMeasurements].GetProperty("zoneIds").EnumerateArray().Select(z => z.GetGuid()));
        Assert.True(functions[PermissionFunctions.SupplierPayments].GetProperty("allZones").GetBoolean());
        Assert.DoesNotContain(functions.Keys, f => f.StartsWith("DASHBOARD_", StringComparison.Ordinal));
        Assert.Equal(
            new[] { (zoneA, FixedRoles.ZoneManagerCode), (zoneC, FixedRoles.AccountantCode) }.Order(),
            permissions.GetProperty("grants").EnumerateArray().Select(g => (g.GetProperty("zoneId").GetGuid(), g.Text("roleCode")!)).Order());

        // A staff account whose only pair is revoked has no Effective pair and cannot log in.
        var (unpairedId, unpairedUsername, temporaryPassword) = await CreateStaffAsync(owner.Client, zoneA, FixedRoles.ZoneManagerCode);
        var onlyPair = (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/accounts/{unpairedId}/grants"))[0].GetProperty("id").GetGuid();
        await owner.Client.PostAsJsonAsync($"/api/accounts/{unpairedId}/grants/{onlyPair}/revoke", new { reason = "Not starting" });
        var refused = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = unpairedUsername, password = temporaryPassword });
        Assert.Equal("MSG-ACCOUNT-07", await refused.ErrorCodeAsync());
    }

    // AC-02: revoking the Zone B pair keeps it Revoked with its end time and reason, refuses the next action on a raft
    // of Zone B and leaves Zone A unchanged; a lock logs out everywhere and keeps the pairs; after the unlock the Effective
    // pairs work again. Roles and their permissions cannot be created or edited.
    [Fact]
    public async Task Ac02_revoke_lock_and_unlock_keep_the_pairs_and_roles_cannot_be_edited()
    {
        await factory.EnsureFixedRolesAsync();
        var owner = await factory.OwnerAsync();
        var zoneA = (await owner.Client.CreateZoneAsync()).Id();
        var zoneB = (await owner.Client.CreateZoneAsync()).Id();
        var raftA = (await owner.Client.CreateRaftAsync(zoneA)).Id();
        var raftB = (await owner.Client.CreateRaftAsync(zoneB)).Id();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await PairAsync(owner.Client, staff.Id, zoneA);
        var pairB = await PairAsync(owner.Client, staff.Id, zoneB);
        var client = await factory.LoginAsync(staff.Username);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/rafts/{raftB}")).StatusCode);

        var revoke = await owner.Client.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pairB}/revoke", new { reason = "Moved to zone A only" });

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/api/rafts/{raftB}")).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/rafts/{raftA}")).StatusCode);
        var pairs = await PairsAsync(owner.Client, staff.Id);
        Assert.Equal(("Revoked", "Moved to zone A only"), (pairs[zoneB].Text("state"), pairs[zoneB].Text("revokeReason")));
        Assert.NotEqual(JsonValueKind.Null, pairs[zoneB].GetProperty("revokedAt").ValueKind);
        Assert.Equal("Effective", pairs[zoneA].Text("state"));

        // ACCOUNT-09.2: the lock ends every session and keeps the pairs.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.PostAsJsonAsync($"/api/accounts/{staff.Id}/lock", new { reason = "On leave" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/rafts/{raftA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password })).StatusCode);
        Assert.Equal("Effective", (await PairsAsync(owner.Client, staff.Id))[zoneA].Text("state"));

        // ACCOUNT-09.3: after the unlock the account works again with the pairs that are still Effective.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.PostAsJsonAsync($"/api/accounts/{staff.Id}/unlock", new { reason = "Back" })).StatusCode);
        var again = await factory.LoginAsync(staff.Username);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync($"/api/rafts/{raftA}")).StatusCode);
        Assert.Equal("MSG-GEN-08", await (await again.GetAsync($"/api/rafts/{raftB}")).ErrorCodeAsync());

        // BR-FARM-07: roles are fixed.
        var roleId = (await factory.RoleAsync(StaffRole.ZoneManager)).Id;
        var create = await owner.Client.PostAsJsonAsync("/api/roles", new { name = "Harvest team" });
        var edit = await owner.Client.PutAsJsonAsync($"/api/roles/{roleId}", new { name = "Renamed" });
        Assert.True(create.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{(int)create.StatusCode}");
        Assert.True(edit.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{(int)edit.StatusCode}");
    }

    private static async Task<(Guid Id, string Username, string TemporaryPassword)> CreateStaffAsync(HttpClient owner, Guid zoneId, string role)
    {
        var username = $"s{NewCode()}";
        var response = await owner.PostAsJsonAsync("/api/accounts", new
        {
            displayName = "Nguyễn Văn D",
            username,
            phoneNumber = NewPhone(),
            grants = new[] { new { zoneId, role } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("account").GetProperty("id").GetGuid(), username, body.Text("temporaryPassword")!);
    }

    private static async Task<Guid> PairAsync(HttpClient owner, Guid accountId, Guid zoneId)
    {
        var response = await owner.PostAsJsonAsync($"/api/accounts/{accountId}/grants", new { zoneId, role = FixedRoles.ZoneManagerCode });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Dictionary<Guid, JsonElement>> PairsAsync(HttpClient owner, Guid accountId) =>
        (await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{accountId}/grants")).EnumerateArray()
            .ToDictionary(p => p.GetProperty("zoneId").GetGuid());

    /// <summary>Stands for the first-login password change and phone check (ACCOUNT-04, ACCOUNT-06), tested on their own.</summary>
    private async Task UseOwnPasswordAsync(Guid accountId)
    {
        var hash = factory.Services.GetRequiredService<IPasswordHasher>().Hash(Password);
        await factory.UpdateAsync<Account>(accountId, a =>
        {
            a.PasswordHash = hash;
            a.MustChangePassword = false;
            a.PhoneVerifiedAt = DateTimeOffset.UtcNow;
        });
    }
}
