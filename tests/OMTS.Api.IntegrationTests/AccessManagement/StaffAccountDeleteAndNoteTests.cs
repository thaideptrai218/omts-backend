using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>
/// Owner decisions of 26/11/2026: "Xóa người dùng" is a soft delete (status Deleted, the row stays), and an account has a free "Ghi chú" and may be created
/// with no employee at all.
/// </summary>
public class StaffAccountDeleteAndNoteTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Delete_hides_the_account_ends_its_sessions_revokes_its_pairs_and_keeps_the_row_and_the_name()
    {
        var (owner, zone, role) = await SetupAsync();
        var created = await CreateAsync(owner, new { displayName = "Lê Văn Xóa", username = $"s{NewCode()}", phoneNumber = NewPhone(), grants = new[] { new { zoneId = zone.Id, roleId = role.Id } } });
        var id = created.GetProperty("account").GetProperty("id").GetGuid();
        var username = created.GetProperty("account").GetProperty("username").GetString()!;
        var session = await factory.LoginAsync(username, created.GetProperty("temporaryPassword").GetString()!);

        var response = await owner.PostAsJsonAsync($"/api/accounts/{id}/delete", new { reason = "Nghỉ việc" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("deleted").GetBoolean());
        Assert.Equal("Deleted", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("pairs").EnumerateArray());
        // Gone from the list, still there with includeDeleted, still readable by id with its name.
        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/accounts?search={username}");
        var withDeleted = await owner.GetFromJsonAsync<JsonElement>($"/api/accounts?search={username}&includeDeleted=true");
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Equal("Lê Văn Xóa", Assert.Single(withDeleted.GetProperty("items").EnumerateArray()).GetProperty("displayName").GetString());
        Assert.Equal("Lê Văn Xóa", (await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}")).GetProperty("displayName").GetString());
        // No login, the open session ends, the pairs are revoked with the reason, the status change is audited with the reason.
        Assert.NotEqual(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = created.GetProperty("temporaryPassword").GetString() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/me")).StatusCode);
        var stored = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal(StatusIds.Account.Deleted, stored.StatusId);
        Assert.False(await factory.QueryAsync(db => db.LoginSessions.AnyAsync(s => s.AccountId == id && s.RevokedAt == null)));
        var grant = Assert.Single(await factory.QueryAsync(db => db.AccessGrants.AsNoTracking().Where(g => g.AccountId == id).ToListAsync()));
        Assert.Equal(StatusIds.AccessGrant.Revoked, grant.StatusId);
        Assert.Contains("Nghỉ việc", grant.RevokeReason);
        Assert.True(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordId == id && e.Action == "Account.StatusChanged" && e.Reason == "Nghỉ việc")));
    }

    [Fact]
    public async Task Delete_needs_a_reason_refuses_a_stale_version_a_second_delete_and_every_other_change()
    {
        var (owner, zone, role) = await SetupAsync();
        var created = await CreateAsync(owner, new { displayName = "A", username = $"s{NewCode()}", phoneNumber = NewPhone(), grants = new[] { new { zoneId = zone.Id, roleId = role.Id } } });
        var account = created.GetProperty("account");
        var id = account.GetProperty("id").GetGuid();

        var noReason = await owner.PostAsJsonAsync($"/api/accounts/{id}/delete", new { reason = " " });
        var stale = await owner.PostAsJsonAsync($"/api/accounts/{id}/delete", new { reason = "x", rowVersion = StaleRowVersion });
        var deleted = await owner.PostAsJsonAsync($"/api/accounts/{id}/delete", new { reason = "x", rowVersion = account.GetProperty("rowVersion").GetString() });
        var again = await owner.PostAsJsonAsync($"/api/accounts/{id}/delete", new { reason = "y" });
        var edit = await owner.PutAsJsonAsync($"/api/accounts/{id}", new { displayName = "B", username = account.GetProperty("username").GetString(), phoneNumber = account.GetProperty("phoneNumber").GetString(), rowVersion = (await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}")).GetProperty("rowVersion").GetString() });
        var reset = await owner.PostAsync($"/api/accounts/{id}/reset-password", null);
        var unlock = await owner.PostAsJsonAsync($"/api/accounts/{id}/unlock", new { reason = "z" });
        var unknown = await owner.PostAsJsonAsync($"/api/accounts/{Guid.NewGuid()}/delete", new { reason = "x" });

        Assert.Equal("MSG-GEN-01", await noReason.ErrorCodeAsync());
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(("MSG-GEN-09", "MSG-GEN-09", "MSG-GEN-09", "MSG-GEN-09"), (await again.ErrorCodeAsync(), await edit.ErrorCodeAsync(), await reset.ErrorCodeAsync(), await unlock.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Delete_refuses_an_owner_account_the_callers_own_account_and_anyone_but_the_owner()
    {
        var (owner, ownerAccount, zone, role) = await SetupWithAccountAsync();
        var otherOwner = await factory.AddAccountAsync(NewCode(), Password);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var manager = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(manager, StaffRole.ZoneManager, zone);

        var self = await owner.PostAsJsonAsync($"/api/accounts/{ownerAccount.Id}/delete", new { reason = "x" });
        var other = await owner.PostAsJsonAsync($"/api/accounts/{otherOwner.Id}/delete", new { reason = "x" });
        var asTrader = await owner.PostAsJsonAsync($"/api/accounts/{trader.Id}/delete", new { reason = "x" });
        var byManager = await (await factory.LoginAsync(manager.Username)).PostAsJsonAsync($"/api/accounts/{manager.Id}/delete", new { reason = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asTrader.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byManager.StatusCode);
        var statuses = await factory.QueryAsync(db => db.Accounts.AsNoTracking().Where(a => new[] { ownerAccount.Id, otherOwner.Id, trader.Id, manager.Id }.Contains(a.Id)).Select(a => a.StatusId).ToListAsync());
        Assert.DoesNotContain(StatusIds.Account.Deleted, statuses);
    }

    [Fact]
    public async Task Delete_unlinks_the_employee_and_keeps_the_username_phone_and_email_reserved()
    {
        var (owner, zone, role) = await SetupAsync();
        var employee = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.StaffProfile($"Có tài khoản {NewCode()}", NewPhone(), zone.Id));
        var employeeId = EmployeeRequests.IdOf(employee);
        var (username, phone, email) = ($"s{NewCode()}", NewPhone(), $"{NewCode()}@example.com");
        var created = await CreateAsync(owner, new { displayName = "Có NV", username, phoneNumber = phone, email, employeeId, grants = new[] { new { zoneId = zone.Id, roleId = role.Id } } });
        var id = created.GetProperty("account").GetProperty("id").GetGuid();
        Assert.Equal(employeeId, created.GetProperty("account").GetProperty("employeeId").GetGuid());

        var deleted = await (await owner.PostAsJsonAsync($"/api/accounts/{id}/delete", new { reason = "Nghỉ" })).Content.ReadFromJsonAsync<JsonElement>();
        var again = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Mới", username, phoneNumber = NewPhone() });
        var samePhone = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Mới", username = $"s{NewCode()}", phoneNumber = phone });
        var sameEmail = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Mới", username = $"s{NewCode()}", phoneNumber = NewPhone(), email });

        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("employeeId").ValueKind);
        Assert.Null((await factory.QueryAsync(db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId))).AccountId);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, samePhone.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);
        // The employee can be given another account.
        var next = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Có NV 2", username = $"s{NewCode()}", phoneNumber = NewPhone(), employeeId });
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }

    [Fact]
    public async Task An_account_with_no_employee_takes_a_note_and_the_roles_and_zones_of_a_form()
    {
        var (owner, zone, role) = await SetupAsync();
        var accountant = await factory.RoleAsync(StaffRole.Accountant);

        var created = await CreateAsync(owner, new
        {
            displayName = "Kế toán HTX", username = $"kt{NewCode()}", phoneNumber = NewPhone(), email = $"{NewCode()}@example.com", note = "  Làm từ thứ hai  ",
            grants = new object[] { new { zoneId = zone.Id, roleId = role.Id }, new { zoneId = (Guid?)null, roleId = accountant.Id } },
        });
        var account = created.GetProperty("account");
        var id = account.GetProperty("id").GetGuid();

        Assert.Equal(JsonValueKind.Null, account.GetProperty("employeeId").ValueKind);
        Assert.Equal("Làm từ thứ hai", account.GetProperty("note").GetString());
        Assert.False(account.GetProperty("deleted").GetBoolean());
        Assert.Equal(2, account.GetProperty("pairs").GetArrayLength());
        // A whole-form edit keeps the note when it is sent and clears it when it is blank.
        async Task<JsonElement> EditAsync(string? note)
        {
            var current = await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}");
            var saved = await owner.PutAsJsonAsync($"/api/accounts/{id}", new
            {
                displayName = "Kế toán HTX", username = account.GetProperty("username").GetString(), phoneNumber = account.GetProperty("phoneNumber").GetString(), email = account.GetProperty("email").GetString(), note,
                rowVersion = current.GetProperty("rowVersion").GetString(),
            });
            return await saved.Content.ReadFromJsonAsync<JsonElement>();
        }

        var changed = await EditAsync("Đổi ghi chú");
        var cleared = await EditAsync(" ");
        Assert.Equal("Đổi ghi chú", changed.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task A_note_longer_than_500_characters_is_refused_on_create_and_edit()
    {
        var (owner, _, _) = await SetupAsync();
        var created = await CreateAsync(owner, new { displayName = "N", username = $"s{NewCode()}", phoneNumber = NewPhone() });
        var account = created.GetProperty("account");
        var tooLong = new string('x', 501);

        var onCreate = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "N2", username = $"s{NewCode()}", phoneNumber = NewPhone(), note = tooLong });
        var onEdit = await owner.PutAsJsonAsync($"/api/accounts/{account.GetProperty("id").GetGuid()}", new
        {
            displayName = "N", username = account.GetProperty("username").GetString(), phoneNumber = account.GetProperty("phoneNumber").GetString(), rowVersion = account.GetProperty("rowVersion").GetString(), note = tooLong,
        });

        Assert.Equal("MSG-GEN-01", await onCreate.ErrorCodeAsync());
        Assert.Equal("Note", (await onCreate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal("MSG-GEN-01", await onEdit.ErrorCodeAsync());
    }

    private async Task<(HttpClient Owner, FarmingZone Zone, Role Role)> SetupAsync()
    {
        var (client, _, zone, role) = await SetupWithAccountAsync();
        return (client, zone, role);
    }

    private async Task<(HttpClient Owner, Account Account, FarmingZone Zone, Role Role)> SetupWithAccountAsync()
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.RoleAsync(StaffRole.ZoneManager);
        return (await factory.LoginAsync(owner.Username), owner, zone, role);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient owner, object body)
    {
        var response = await owner.PostAsJsonAsync("/api/accounts", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
