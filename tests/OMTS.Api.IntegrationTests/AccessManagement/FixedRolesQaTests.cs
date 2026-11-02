using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.Filters;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>
/// QA of the fixed roles and the FARM-05 rules (OMTS-216 PR 0.3, SRS 27/10/2026 §I.4.4, BR-FARM-07/08, AC-01/AC-02):
/// role rows left from before the fixed roles, revoking, locked accounts and two roles of one account in two zones.
/// </summary>
public class FixedRolesQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // P-6 widened NoZone to "any pair of the role": it is only safe on functions whose records really have no zone.
    // A NoZone check on a zone-scoped function would open every zone to a one-zone pair.
    [Fact]
    public void Every_no_zone_check_is_on_a_function_whose_records_have_no_zone()
    {
        var checks = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RequirePermissionAttribute>(true)
                .Concat(t.GetMethods().SelectMany(m => m.GetCustomAttributes<RequirePermissionAttribute>(true))))
            .ToList();

        Assert.Contains(checks, c => c.Scope == PermissionScope.NoZone);
        Assert.All(
            checks.Where(c => c.Scope == PermissionScope.NoZone),
            c => Assert.True(PermissionFunctions.IsCooperativeWide(c.Function), $"{c.Function} {c.Action} is checked with NoZone but its records have a zone"));
    }

    // BR-FARM-07: a role row kept from before (placeholder code) is not a fixed role: its pairs add no right next to a
    // real pair, and it cannot be granted, by id or by code.
    [Fact]
    public async Task Old_role_pair_adds_no_right_and_the_old_role_cannot_be_granted()
    {
        var owner = await OwnerAsync();
        var legacy = await LegacyRoleAsync();
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, zoneA);
        await LegacyPairAsync(staff, legacy, zoneId: null);
        var client = await factory.LoginAsync(staff.Username);

        var functions = (await client.GetFromJsonAsync<JsonElement>("/api/me/permissions")).GetProperty("functions").EnumerateArray()
            .ToDictionary(f => f.Text("function")!);
        Assert.Equal([zoneA.Id], functions[PermissionFunctions.WaterMeasurements].GetProperty("zoneIds").EnumerateArray().Select(z => z.GetGuid()));
        Assert.False(functions[PermissionFunctions.WaterMeasurements].GetProperty("allZones").GetBoolean());
        Assert.DoesNotContain(PermissionFunctions.SupplierPayments, functions.Keys);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/test-only/zones/{zoneB.Id}/water-measurements")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/test-only/supplier-payments")).StatusCode);

        var other = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        foreach (var draft in new object[] { new { zoneId = zoneA.Id, roleId = legacy.Id }, new { zoneId = zoneA.Id, role = legacy.Code } })
        {
            var refused = await owner.PostAsJsonAsync($"/api/accounts/{other.Id}/grants", draft);
            Assert.Equal("MSG-GEN-01", await refused.ErrorCodeAsync());
            Assert.Equal("Role", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        Assert.Empty(await factory.QueryAsync(db => db.AccessGrants.Where(g => g.AccountId == other.Id).ToListAsync()));
    }

    // AC-01, BR-ACCOUNT-05: a staff account whose only Effective pair is of a role outside the fixed roles holds no role
    // at all, so it must be refused like an account with no pair (MSG-ACCOUNT-07). The migration keeps such pairs
    // Effective (placeholder role code), and HasEffectiveGrantAsync counts any Effective pair.
    [Fact]
    public async Task Staff_whose_only_pair_is_of_an_old_role_cannot_log_in()
    {
        await factory.EnsureFixedRolesAsync();
        var legacy = await LegacyRoleAsync();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await LegacyPairAsync(staff, legacy, zoneId: null);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("MSG-ACCOUNT-07", await login.ErrorCodeAsync());
    }

    // NFR-01, BR-FARM-08: zones and rafts are read in the zones of the pairs. A pair of an old role gives no role, so it
    // must not open zones either (FarmingLookupAccess counts every Effective pair, of any role row).
    [Fact]
    public async Task Old_role_pair_for_all_zones_opens_no_zone_or_raft()
    {
        await factory.EnsureFixedRolesAsync();
        var legacy = await LegacyRoleAsync();
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var raftB = new Raft { ZoneId = zoneB.Id, Code = NewCode(), Size = "40 m x 20 m" };
        await factory.UseDbAsync(db => db.Rafts.Add(raftB));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, zoneA);
        await LegacyPairAsync(staff, legacy, zoneId: null);
        var client = await factory.LoginAsync(staff.Username);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/zones/{zoneA.Id}")).StatusCode);
        foreach (var path in new[] { $"/api/zones/{zoneB.Id}", $"/api/rafts/{raftB.Id}" })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{path}: {(int)response.StatusCode}");
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }

        var zones = await client.GetFromJsonAsync<JsonElement>("/api/zones/lookup");
        Assert.DoesNotContain(zones.EnumerateArray(), z => z.GetProperty("id").GetGuid() == zoneB.Id);
    }

    // BR-FARM-07, client change list §1: GET /api/roles is "always the 2 fixed roles"; a row kept from before is not one.
    [Fact]
    public async Task Roles_api_shows_only_the_two_fixed_roles_when_an_old_row_remains()
    {
        var owner = await OwnerAsync();
        var legacy = await LegacyRoleAsync();

        var roles = await owner.GetFromJsonAsync<JsonElement>("/api/roles");
        var one = await owner.GetAsync($"/api/roles/{legacy.Id}");

        Assert.Equal([FixedRoles.AccountantCode, FixedRoles.ZoneManagerCode], roles.EnumerateArray().Select(r => r.Text("code")).Order());
        Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
    }

    // FARM-05.1: a revoke needs a reason (MSG-GEN-01, nothing changes); a pair is found only under its own account.
    [Fact]
    public async Task Revoke_needs_a_reason_and_works_only_under_the_account_of_the_pair()
    {
        var owner = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var other = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var pair = await PairAsync(owner, staff.Id, zone.Id, FixedRoles.ZoneManagerCode);
        var client = await factory.LoginAsync(staff.Username);

        foreach (var reason in new[] { null, "", "   ", new string('x', 1001) })
        {
            var refused = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pair}/revoke", new { reason });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("MSG-GEN-01", await refused.ErrorCodeAsync());
            Assert.Equal("Reason", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        var wrongAccount = await owner.PostAsJsonAsync($"/api/accounts/{other.Id}/grants/{pair}/revoke", new { reason = "Wrong account" });
        Assert.Equal(HttpStatusCode.NotFound, wrongAccount.StatusCode);
        Assert.Equal(StatusIds.AccessGrant.Effective, await StatusOfAsync(pair));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).StatusCode);

        var revoked = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pair}/revoke", new { reason = "  Season over  " });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("Season over", (await revoked.Content.ReadFromJsonAsync<JsonElement>()).Text("revokeReason"));
        Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/test-only/zones/{zone.Id}/water-measurements")).ErrorCodeAsync());
        var audit = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == pair).ToListAsync());
        Assert.Contains(audit, e => e.Reason == "Season over" && e.ZoneId == zone.Id);
    }

    // ACCOUNT-09.2/09.3, FARM-05: no pair is granted to a Locked account (MSG-GEN-09), but its pairs can still be revoked;
    // after the unlock only the pairs still Effective work, and with none left the login is refused (MSG-ACCOUNT-07).
    [Fact]
    public async Task Pair_of_a_locked_account_can_be_revoked_and_stays_revoked_after_the_unlock()
    {
        var owner = await OwnerAsync();
        var (zoneA, zoneB, zoneC) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var pairA = await PairAsync(owner, staff.Id, zoneA.Id, FixedRoles.ZoneManagerCode);
        var pairB = await PairAsync(owner, staff.Id, zoneB.Id, FixedRoles.ZoneManagerCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/lock", new { reason = "On leave" })).StatusCode);

        var revokeWhileLocked = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pairB}/revoke", new { reason = "Zone B handed over" });
        var grantWhileLocked = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = zoneC.Id, role = FixedRoles.ZoneManagerCode });

        Assert.Equal(HttpStatusCode.OK, revokeWhileLocked.StatusCode);
        Assert.Equal("Revoked", (await revokeWhileLocked.Content.ReadFromJsonAsync<JsonElement>()).Text("state"));
        Assert.Equal("MSG-GEN-09", await grantWhileLocked.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/unlock", new { reason = "Back" })).StatusCode);
        var client = await factory.LoginAsync(staff.Username);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test-only/zones/{zoneA.Id}/water-measurements")).StatusCode);
        Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/test-only/zones/{zoneB.Id}/water-measurements")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/test-only/zones/{zoneC.Id}/water-measurements")).ErrorCodeAsync());
        Assert.Equal(StatusIds.AccessGrant.Revoked, await StatusOfAsync(pairB));

        await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pairA}/revoke", new { reason = "Left the cooperative" });
        var refused = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password });
        Assert.Equal("MSG-ACCOUNT-07", await refused.ErrorCodeAsync());
    }

    // AC-01 on live endpoints: Zone A – Zone Manager and Zone C – Accountant. Purchase receipts have a zone in this
    // version: Zone Manager rights in A, view only in C (PURCHASE-03 R), nothing in B. Suppliers and supplier payments have
    // no zone and follow each role (BR-FARM-08).
    [Fact]
    public async Task Zone_manager_and_accountant_pairs_keep_receipts_to_their_own_zones_and_actions()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (a, b, c) = (await factory.WorldAsync(owner), await factory.WorldAsync(owner), await factory.WorldAsync(owner));
        var receiptA = (await a.CompletedAsync(0, 0, new LineBody(a.Seed, 1, 1_000))).Id();
        var receiptB = (await b.CompletedAsync(0, 0, new LineBody(b.Seed, 1, 1_000))).Id();
        var receiptC = (await c.CompletedAsync(0, 0, new LineBody(c.Seed, 1, 1_000))).Id();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, a.Zone);
        await factory.GrantAsync(staff, StaffRole.Accountant, c.Zone);
        var client = await factory.LoginAsync(staff.Username);

        await (await client.GetAsync($"/api/purchase-receipts/{receiptA}")).ReadAsync(HttpStatusCode.OK);
        await (await client.GetAsync($"/api/purchase-receipts/{receiptC}")).ReadAsync(HttpStatusCode.OK);
        await (await client.GetAsync($"/api/purchase-receipts/{receiptB}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await client.GetAsync($"/api/purchase-receipts?zoneId={b.Zone.Id}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        var listed = (await (await client.GetAsync("/api/purchase-receipts?pageSize=100")).ReadAsync(HttpStatusCode.OK))
            .GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Id()).ToList();
        Assert.Contains(receiptA, listed);
        Assert.Contains(receiptC, listed);
        Assert.DoesNotContain(receiptB, listed);

        // Writes and export only where the Zone Manager pair is.
        await (await client.PostReceiptAsync(c.Body(new LineBody(c.Other, 1, 1_000)))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await client.CancelAsync(receiptC, "Wrong supplier", false)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await client.GetAsync($"/api/purchase-receipts/export?zoneId={c.Zone.Id}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/purchase-receipts/export?zoneId={a.Zone.Id}")).StatusCode);
        await (await client.PostReceiptAsync(a.Body(new LineBody(a.Other, 1, 1_000)))).ReadAsync(HttpStatusCode.Created);

        // No zone: suppliers by the Zone Manager role, supplier payments and debts by the Accountant role, in every zone.
        await (await client.GetAsync($"/api/suppliers/{b.SupplierId}/debt")).ReadAsync(HttpStatusCode.OK);
        await (await client.PostPaymentAsync(new PaymentBody { SupplierId = b.SupplierId, ReceiptId = receiptB, Amount = 1_000 })).ReadAsync(HttpStatusCode.Created);
        await client.CreateSupplierAsync();
    }

    private async Task<HttpClient> OwnerAsync()
    {
        await factory.EnsureFixedRolesAsync();
        return await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
    }

    /// <summary>A role row as the FixedRoles migration leaves an Owner-made role: placeholder code, no permission.</summary>
    private async Task<Role> LegacyRoleAsync()
    {
        var role = new Role { Code = $"LEGACY-{NewCode()}", Name = $"Harvest team {NewCode()}" };
        await factory.UseDbAsync(db => db.Roles.Add(role));
        return role;
    }

    private Task LegacyPairAsync(Account staff, Role legacy, Guid? zoneId) => factory.UseDbAsync(db => db.AccessGrants.Add(new AccessGrant
    {
        AccountId = staff.Id,
        RoleId = legacy.Id,
        ZoneId = zoneId,
        ScopeType = zoneId is null ? AccessScopeType.AllZones : AccessScopeType.SpecificZone,
        GrantedByAccountId = staff.Id,
    }));

    private static async Task<Guid> PairAsync(HttpClient owner, Guid accountId, Guid zoneId, string role)
    {
        var response = await owner.PostAsJsonAsync($"/api/accounts/{accountId}/grants", new { zoneId, role });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<int> StatusOfAsync(Guid grantId) =>
        factory.QueryAsync(db => db.AccessGrants.Where(g => g.Id == grantId).Select(g => g.StatusId).SingleAsync());
}
