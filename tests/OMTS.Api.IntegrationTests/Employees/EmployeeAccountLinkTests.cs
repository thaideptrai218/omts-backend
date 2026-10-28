using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// LAB-01 account link (MSG24, linkable accounts) and the login account created with the employee or later
/// from the profile (ACS-09 extends LAB-01): defaults, temporary password once, all-or-nothing saves.
/// </summary>
public class EmployeeAccountLinkTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Account_linked_elsewhere_names_that_employee_on_create_and_on_update()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var holderName = $"Holder {NewCode()}";
        var holder = IdOf(await CreateAsync(owner, Profile(holderName, NewPhone(), zone.Id, b => b["accountId"] = staff.Id)));
        var later = await CreateAsync(owner, Profile($"Later {NewCode()}", NewPhone(), zone.Id));
        var newName = $"New {NewCode()}";

        var onCreate = await owner.PostAsJsonAsync("/api/employees", Profile(newName, NewPhone(), zone.Id, b => b["accountId"] = staff.Id));
        var onUpdate = await owner.PutAsJsonAsync($"/api/employees/{IdOf(later)}",
            Profile($"Later {NewCode()}", NewPhone(), zone.Id, b => { b["accountId"] = staff.Id; b["rowVersion"] = RowVersionOf(later); }));

        foreach (var conflict in new[] { onCreate, onUpdate })
        {
            var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("MSG24", problem.GetProperty("code").GetString());
            Assert.Equal($"This account is already linked to the employee profile {holderName}.", problem.GetProperty("detail").GetString());
            Assert.Equal(holder, problem.GetProperty("existingEmployeeId").GetGuid());
        }

        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == newName)));
        Assert.Equal([holder], await factory.QueryAsync(db => db.Employees.Where(e => e.AccountId == staff.Id).Select(e => e.Id).ToListAsync()));
    }

    [Fact]
    public async Task Linkable_accounts_are_unlinked_staff_only_and_linking_grants_no_permission()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var free = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var otherOwner = await factory.AddAccountAsync(NewCode(), Password);

        var before = await owner.GetFromJsonAsync<JsonElement>("/api/employees/linkable-accounts");
        var row = before.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == free.Id);
        await CreateAsync(owner, Profile($"Linked {NewCode()}", NewPhone(), zone.Id, b => b["accountId"] = free.Id));
        var after = await owner.GetFromJsonAsync<JsonElement>("/api/employees/linkable-accounts");
        // A staff account without zone–role grants cannot work; being linked to an employee changes nothing.
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = free.Username, password = Password });

        Assert.Equal((free.DisplayName, free.Username, free.PhoneNumber, "Active"),
            (row.GetProperty("displayName").GetString(), row.GetProperty("username").GetString(), row.GetProperty("phoneNumber").GetString(), row.GetProperty("status").GetString()));
        Assert.DoesNotContain(before.EnumerateArray(), a => a.GetProperty("id").GetGuid() == trader.Id || a.GetProperty("id").GetGuid() == otherOwner.Id);
        Assert.DoesNotContain(after.EnumerateArray(), a => a.GetProperty("id").GetGuid() == free.Id);
        Assert.Equal("MSG-ACCOUNT-07", await login.ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.AccessGrants.AnyAsync(g => g.AccountId == free.Id)));
    }

    [Fact]
    public async Task New_account_defaults_to_the_employee_name_and_phone_and_its_password_is_shown_once()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var (name, phone, username) = ($"Đỗ Thị {NewCode()}", NewPhone(), $"nv{NewCode()}");

        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id, b => b["account"] = AccountBlock(username, zone, role)));
        var id = IdOf(saved);
        var reread = await owner.GetAsync($"/api/employees/{id}");
        var account = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Username == username));
        var temporaryPassword = saved.GetProperty("temporaryPassword").GetString()!;

        Assert.Equal((name, phone, SubjectType.Staff, true), (account.DisplayName, account.PhoneNumber, account.SubjectType, account.MustChangePassword));
        Assert.Equal(account.Id, await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        Assert.Equal(10, temporaryPassword.Length);
        var expiresAt = saved.GetProperty("temporaryPasswordExpiresAt").GetDateTimeOffset();
        Assert.InRange(expiresAt - DateTimeOffset.UtcNow, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));
        Assert.DoesNotContain(temporaryPassword, await reread.Content.ReadAsStringAsync());
        Assert.DoesNotContain("temporaryPassword", await reread.Content.ReadAsStringAsync());
        Assert.Single(await factory.QueryAsync(db => db.AccessGrants.Where(g => g.AccountId == account.Id && g.ZoneId == zone.Id && g.RoleId == role.Id).ToListAsync()));
        Assert.Empty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone));
        Assert.DoesNotContain(factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List(), m => m.Text.Contains(temporaryPassword));
    }

    [Fact]
    public async Task Taken_phone_empty_grants_and_unusable_role_or_zone_save_neither_employee_nor_account()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var taken = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        // Only the fixed roles exist (BR-FARM-07): any other role id is refused.
        var unknownRoleId = Guid.NewGuid();
        var closedZone = await factory.AddZoneAsync(NewCode());
        await factory.UpdateAsync<FarmingZone>(closedZone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);
        var usernames = Enumerable.Range(0, 4).Select(_ => $"nv{NewCode()}").ToArray();
        var cases = new (string Name, string Username, object Account, string Expected)[]
        {
            ($"P {NewCode()}", usernames[0], AccountBlock(usernames[0], zone, role, phoneNumber: taken.PhoneNumber), "MSG-ACCOUNT-09"),
            ($"Q {NewCode()}", usernames[1], new { username = usernames[1], grants = Array.Empty<object>() }, "Zone-role pair"),
            ($"R {NewCode()}", usernames[2], AccountBlock(usernames[2], zone, unknownRoleId), "Role"),
            ($"S {NewCode()}", usernames[3], AccountBlock(usernames[3], closedZone, role), "Zone"),
        };

        foreach (var (name, username, account, expected) in cases)
        {
            var response = await owner.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id, b => b["account"] = account));
            if (expected == "MSG-ACCOUNT-09")
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("MSG-ACCOUNT-09", problem.GetProperty("code").GetString());
                Assert.Equal("This phone number is already used by another account. Please enter another one.", problem.GetProperty("detail").GetString());
                Assert.Equal("Phone number", problem.GetProperty("field").GetString());
            }
            else
            {
                Assert.Equal(expected, await FieldAsync(response));
            }

            Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == name)));
            Assert.False(await factory.QueryAsync(db => db.Accounts.AnyAsync(a => a.Username == username)));
        }
    }

    [Fact]
    public async Task Account_created_later_from_the_profile_is_linked_once_and_owner_rights_are_refused()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var saved = await CreateAsync(owner, Profile($"Later {NewCode()}", NewPhone(), zone.Id));
        var id = IdOf(saved);
        var (username, ownerUsername) = ($"nv{NewCode()}", $"nv{NewCode()}");

        var ownerRights = await owner.PostAsJsonAsync($"/api/employees/{id}/account",
            new { username = ownerUsername, grants = new[] { new { zoneId = (Guid?)null, roleId = role.Id } }, subjectType = "CooperativeOwner" });
        var created = await owner.PostAsJsonAsync($"/api/employees/{id}/account", AccountBlock(username, null, role));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var again = await owner.PostAsJsonAsync($"/api/employees/{id}/account", AccountBlock($"nv{NewCode()}", zone, role));

        Assert.Equal("MSG-GEN-08", await ownerRights.ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.Accounts.AnyAsync(a => a.Username == ownerUsername)));
        Assert.True(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Account.OwnerRightsRefused" && e.RecordCode == ownerUsername)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(username, body.GetProperty("employee").GetProperty("account").GetProperty("username").GetString());
        Assert.Equal(10, body.GetProperty("temporaryPassword").GetString()!.Length);
        Assert.Equal("Account", await FieldAsync(again));
        var accountId = await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync());
        Assert.Equal(await factory.QueryAsync(db => db.Accounts.Where(a => a.Username == username).Select(a => (Guid?)a.Id).SingleAsync()), accountId);
        Assert.Equal(1, await factory.QueryAsync(db => db.AccessGrants.CountAsync(g => g.AccountId == accountId && g.ScopeType == AccessScopeType.AllZones)));
    }
}
