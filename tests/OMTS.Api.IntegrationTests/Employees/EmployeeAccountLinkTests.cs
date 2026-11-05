using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// LABOR-01.5: the staff account created with the employee, by the Cooperative Owner only: the name and number of the
/// employee, the temporary password once, no pair until FARM-05, all-or-nothing saves.
/// </summary>
public class EmployeeAccountLinkTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task New_account_has_the_employee_name_and_phone_and_its_password_is_shown_once()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var (name, phone, username) = ($"Đỗ Thị {NewCode()}", NewPhone(), $"nv{NewCode()}");

        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id, b => b["account"] = AccountBlock(username, zone, role)));
        var id = IdOf(saved);
        var reread = await owner.GetAsync($"/api/employees/{id}");
        var account = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Username == username));
        var temporaryPassword = saved.GetProperty("temporaryPassword").GetString()!;

        Assert.Equal((name, phone, SubjectType.Staff, true), (account.DisplayName, account.PhoneNumber, account.SubjectType, account.MustChangePassword));
        Assert.Null(account.PhoneVerifiedAt);
        Assert.Equal(account.Id, await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        Assert.True(saved.GetProperty("employee").GetProperty("hasStaffAccount").GetBoolean());
        Assert.Equal(10, temporaryPassword.Length);
        var expiresAt = saved.GetProperty("temporaryPasswordExpiresAt").GetDateTimeOffset();
        Assert.InRange(expiresAt - DateTimeOffset.UtcNow, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));
        Assert.DoesNotContain(temporaryPassword, await reread.Content.ReadAsStringAsync());
        Assert.DoesNotContain("temporaryPassword", await reread.Content.ReadAsStringAsync());
        Assert.Single(await factory.QueryAsync(db => db.AccessGrants.Where(g => g.AccountId == account.Id && g.ZoneId == zone.Id && g.RoleId == role.Id).ToListAsync()));
        Assert.Empty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone));
        Assert.DoesNotContain(factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List(), m => m.Text.Contains(temporaryPassword));
        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => (e.After ?? "").Contains(temporaryPassword))));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains(temporaryPassword));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Account.Created" && e.RecordId == account.Id)));
    }

    // POST-4: Active, with a temporary password and no pair; the staff member can log in after a pair is granted in FARM-05.
    [Fact]
    public async Task An_account_without_a_pair_is_created_and_cannot_log_in_until_one_is_granted()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var username = $"nv{NewCode()}";
        var email = $"{username}@example.test";

        var saved = await CreateAsync(owner, Profile($"No pair {NewCode()}", NewPhone(), zone.Id, b => b["account"] = new { username, email }));
        var account = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Username == username));
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = saved.GetProperty("temporaryPassword").GetString() });

        Assert.Equal((StatusIds.Account.Active, email, (DateTimeOffset?)null), (account.StatusId, account.Email, account.EmailVerifiedAt));
        Assert.False(await factory.QueryAsync(db => db.AccessGrants.AnyAsync(g => g.AccountId == account.Id)));
        Assert.Equal("MSG-ACCOUNT-07", await login.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_zone_manager_cannot_create_a_staff_account_or_give_it_owner_rights()
    {
        var (_, zone, role) = await SetupAsync(factory);
        var manager = await ManagerAsync(factory, zone);
        var (name, username) = ($"By manager {NewCode()}", $"nv{NewCode()}");

        var refused = await manager.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id, b => { b["ownerApproved"] = true; b["account"] = AccountBlock(username, zone, role); }));

        Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == name)));
        Assert.False(await factory.QueryAsync(db => db.Accounts.AnyAsync(a => a.Username == username)));
    }

    // LABOR-01.5.E1 and E2: nothing is saved, the client sends the employee again without the account.
    [Fact]
    public async Task Missing_or_taken_data_and_an_unusable_role_or_zone_save_neither_employee_nor_account()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var taken = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        // Only the fixed roles exist (BR-FARM-07): any other role id is refused.
        var unknownRoleId = Guid.NewGuid();
        var closedZone = await factory.AddZoneAsync(NewCode());
        await factory.UpdateAsync<FarmingZone>(closedZone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);
        var fresh = $"nv{NewCode()}";
        var cases = new (string Name, string? Phone, object Account, string Code, string Field)[]
        {
            ($"P {NewCode()}", taken.PhoneNumber, new { username = fresh }, "MSG-ACCOUNT-09", "Phone number"),
            ($"Q {NewCode()}", null, new { username = fresh }, "MSG-GEN-01", "Phone number"),
            ($"T {NewCode()}", NewPhone(), new { username = taken.Username }, "MSG-ACCOUNT-09", "Username"),
            ($"U {NewCode()}", NewPhone(), new { username = "" }, "MSG-GEN-01", "Username"),
            ($"V {NewCode()}", NewPhone(), new { username = fresh, email = "not an email" }, "MSG-GEN-01", "Email"),
            ($"R {NewCode()}", NewPhone(), AccountBlock(fresh, zone, unknownRoleId), "MSG-GEN-01", "Role"),
            ($"S {NewCode()}", NewPhone(), AccountBlock(fresh, closedZone, role), "MSG-GEN-09", "Zone"),
        };

        foreach (var (name, phone, account, code, field) in cases)
        {
            var response = await owner.PostAsJsonAsync("/api/employees", Profile(name, phone, zone.Id, b => b["account"] = account));
            Assert.Equal(field, await FieldAsync(response));
            Assert.Equal(code, await response.ErrorCodeAsync());
            Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == name)));
        }

        Assert.False(await factory.QueryAsync(db => db.Accounts.AnyAsync(a => a.Username == fresh)));
    }

    [Fact]
    public async Task Account_created_later_from_the_profile_is_linked_once_and_owner_rights_are_refused()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var saved = await CreateAsync(owner, Profile($"Later {NewCode()}", NewPhone(), zone.Id));
        var id = IdOf(saved);
        var left = IdOf(await CreateAsync(owner, Profile($"Left {NewCode()}", NewPhone(), zone.Id)));
        await EndAsync(owner, left);
        var (username, ownerUsername) = ($"nv{NewCode()}", $"nv{NewCode()}");

        var ownerRights = await owner.PostAsJsonAsync($"/api/employees/{id}/account",
            new { username = ownerUsername, grants = new[] { new { zoneId = (Guid?)null, roleId = role.Id } }, subjectType = "CooperativeOwner" });
        var created = await owner.PostAsJsonAsync($"/api/employees/{id}/account", AccountBlock(username, null, role));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var again = await owner.PostAsJsonAsync($"/api/employees/{id}/account", AccountBlock($"nv{NewCode()}", zone, role));
        var forLeft = await owner.PostAsJsonAsync($"/api/employees/{left}/account", AccountBlock($"nv{NewCode()}", zone, role));

        Assert.Equal("MSG-GEN-08", await ownerRights.ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.Accounts.AnyAsync(a => a.Username == ownerUsername)));
        Assert.True(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Account.OwnerRightsRefused" && e.RecordCode == ownerUsername)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(username, body.GetProperty("employee").GetProperty("account").GetProperty("username").GetString());
        Assert.Equal(10, body.GetProperty("temporaryPassword").GetString()!.Length);
        Assert.Equal("Account", await FieldAsync(again));
        Assert.Equal("Account", await FieldAsync(forLeft));
        var accountId = await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync());
        Assert.Equal(await factory.QueryAsync(db => db.Accounts.Where(a => a.Username == username).Select(a => (Guid?)a.Id).SingleAsync()), accountId);
        Assert.Equal(1, await factory.QueryAsync(db => db.AccessGrants.CountAsync(g => g.AccountId == accountId && g.ScopeType == AccessScopeType.AllZones)));
    }
}
