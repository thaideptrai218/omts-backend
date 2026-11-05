using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// OMTS-216 PR 0.4, the rules of ACCOUNT-01 to ACCOUNT-09 (SRS 27/10 Part II §7, BR-ACCOUNT-02/03/06/09/10): uniqueness of
/// an unverified email, the phone gate of the Owner and its order, temporary passwords, staff accounts without a pair.
/// </summary>
public class AccountRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string NewAccountPassword = NewPassword();

    // BR-ACCOUNT-02: "the same email, verified or not". Registration answers MSG-ACCOUNT-03, the others MSG-ACCOUNT-09.
    [Fact]
    public async Task An_unverified_email_of_another_account_cannot_be_used_anywhere()
    {
        var email = $"unverified-{NewCode()}@example.com";
        var holder = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.Email = email);
        var ownerAccount = await factory.AddAccountAsync(NewCode(), Password);
        var owner = await factory.LoginAsync(ownerAccount.Username);
        var editedId = (await CreateStaffAsync(owner, null)).GetProperty("account").GetProperty("id").GetGuid();
        var rowVersion = (await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{editedId}")).GetProperty("rowVersion").GetString();

        var registration = await factory.CreateClient().PostAsJsonAsync("/api/verifications", new { channel = "Email", target = email });
        var contactChange = await owner.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = Password });
        var create = await owner.PostAsJsonAsync("/api/accounts", StaffBody(email: email.ToUpperInvariant()));
        var edit = await owner.PutAsJsonAsync($"/api/accounts/{editedId}", new { displayName = "B", username = $"s{NewCode()}", phoneNumber = NewPhone(), email, rowVersion });

        Assert.Equal("MSG-ACCOUNT-03", await registration.ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-09", await contactChange.ErrorCodeAsync());
        foreach (var refused in new[] { create, edit })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("MSG-ACCOUNT-09", body.GetProperty("code").GetString());
            Assert.Equal("Email", body.GetProperty("field").GetString());
            Assert.Equal("This email is already used by another account. Please enter another one.", body.GetProperty("detail").GetString());
            Assert.Equal(holder.Id, body.GetProperty("existingId").GetGuid());
        }
    }

    // An unverified email is not a login and receives no code, and the account itself may verify it.
    [Fact]
    public async Task The_holder_of_an_unverified_email_can_verify_it_and_it_logs_in_only_then()
    {
        var email = $"own-{NewCode()}@example.com";
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.Email = email);
        await factory.GrantAsync(staff, StaffRole.Accountant, null);
        var before = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = email, password = Password });
        var client = await factory.LoginAsync(staff.Username);

        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = Password }));
        var token = await VerifyAsync(client, id, factory.LastEmailCode(email));
        var confirmed = await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token });

        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = email, password = Password })).StatusCode);
    }

    // BR-ACCOUNT-03, ACCOUNT-02.1: the Owner verifies the phone number like every account. The login says so, every other
    // function answers MSG-ACCOUNT-04, and the number on the account can be confirmed without entering it.
    [Fact]
    public async Task The_owner_verifies_the_phone_number_before_any_other_function()
    {
        var phone = NewPhone();
        var ownerAccount = await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.PhoneNumber = phone; a.PhoneVerifiedAt = null; });
        var client = factory.CreateClient();
        var tokens = await LoginTokensAsync(client, ownerAccount.Username);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("accessToken").GetString());

        Assert.True(tokens.GetProperty("phoneVerificationRequired").GetBoolean());
        Assert.Equal("MSG-ACCOUNT-04", await (await client.GetAsync("/api/accounts")).ErrorCodeAsync());
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/me")).ValueKind == JsonValueKind.Object);

        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(phone));
        var confirmed = await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token });

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/accounts")).StatusCode);
        Assert.False((await LoginTokensAsync(factory.CreateClient(), ownerAccount.Username)).GetProperty("phoneVerificationRequired").GetBoolean());
    }

    // ACCOUNT-02 steps 5 and 6: the phone number first, then the temporary password. While the phone is unverified the
    // password change itself answers MSG-ACCOUNT-04; the profile stays open, to show the number entered by the Owner.
    [Fact]
    public async Task The_phone_step_comes_before_the_temporary_password()
    {
        var ownerClient = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.RoleAsync(StaffRole.ZoneManager);
        var phone = NewPhone();
        var created = await CreateStaffAsync(ownerClient, new[] { new { zoneId = zone.Id, roleId = role.Id } }, phone);
        var username = created.GetProperty("account").GetProperty("username").GetString()!;
        var client = factory.CreateClient();
        var tokens = await LoginTokensAsync(client, username, created.GetProperty("temporaryPassword").GetString()!);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("accessToken").GetString());

        Assert.True(tokens.GetProperty("mustChangePassword").GetBoolean());
        Assert.True(tokens.GetProperty("phoneVerificationRequired").GetBoolean());
        Assert.Equal("MSG-ACCOUNT-04", await (await client.GetAsync("/api/me/permissions")).ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-04", await (await client.PostAsJsonAsync("/api/me/password", new { newPassword = NewAccountPassword, passwordConfirmation = NewAccountPassword })).ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-04", await (await client.PostAsync("/api/me/password/code", null)).ErrorCodeAsync());
        Assert.Equal(phone, (await client.GetFromJsonAsync<JsonElement>("/api/me/profile")).GetProperty("phoneNumber").GetString());

        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(phone));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token })).StatusCode);

        Assert.Equal("PASSWORD_CHANGE_REQUIRED", await (await client.GetAsync("/api/me/permissions")).ErrorCodeAsync());
        var changed = await client.PostAsJsonAsync("/api/me/password", new { newPassword = NewAccountPassword, passwordConfirmation = NewAccountPassword });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.False((await LoginTokensAsync(factory.CreateClient(), username, NewAccountPassword)).GetProperty("mustChangePassword").GetBoolean());
    }

    // ACCOUNT-04.1 holds only for the forced change: a normal change still needs its proof.
    [Fact]
    public async Task A_normal_password_change_still_needs_the_current_password()
    {
        var client = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);

        var refused = await client.PostAsJsonAsync("/api/me/password", new { newPassword = NewAccountPassword, passwordConfirmation = NewAccountPassword });

        Assert.Equal("MSG-GEN-01", await refused.ErrorCodeAsync());
        Assert.Equal("Current password", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    // BR-ACCOUNT-06 for staff: the 24 hours hold, and an expired one answers MSG-ACCOUNT-08 (the Owner's first password is
    // in OwnerPasswordResetTests).
    [Fact]
    public async Task A_temporary_password_of_a_staff_account_expires_after_24_hours()
    {
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a =>
        {
            a.MustChangePassword = true;
            a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddHours(-25);
        });
        await factory.GrantAsync(staff, StaffRole.ZoneManager, null);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password });

        Assert.Equal("MSG-ACCOUNT-08", await login.ErrorCodeAsync());
    }

    // ACCOUNT-09 steps 6 to 8, POST-1: created without a pair, with its temporary password (MSG-ACCOUNT-23); login is refused
    // with MSG-ACCOUNT-07 until FARM-05 grants a pair. The list and the detail then show the pair.
    [Fact]
    public async Task A_staff_account_is_created_without_a_pair_and_logs_in_after_the_grant()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var zone = await factory.AddZoneAsync(NewCode());
        var username = $"s{NewCode()}";

        var withoutField = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Nguyễn Văn A", username, phoneNumber = NewPhone() });
        var created = await withoutField.Content.ReadFromJsonAsync<JsonElement>();
        var account = created.GetProperty("account");
        var id = account.GetProperty("id").GetGuid();
        var temporaryPassword = created.GetProperty("temporaryPassword").GetString()!;

        Assert.Equal(HttpStatusCode.Created, withoutField.StatusCode);
        Assert.Equal("MSG-ACCOUNT-23", created.GetProperty("messageCode").GetString());
        Assert.Empty(account.GetProperty("pairs").EnumerateArray());
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/accounts", StaffBody(grants: Array.Empty<object>()))).StatusCode);

        // The version in the answer of the create is the one a first edit sends back.
        var edited = await owner.PutAsJsonAsync($"/api/accounts/{id}", new
        {
            displayName = "Nguyễn Văn B", username, phoneNumber = account.GetProperty("phoneNumber").GetString(), rowVersion = account.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var refused = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = temporaryPassword });
        Assert.Equal("MSG-ACCOUNT-07", await refused.ErrorCodeAsync());

        var granted = await owner.PostAsJsonAsync($"/api/accounts/{id}/grants", new { zoneId = zone.Id, role = FixedRoles.AccountantCode });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}");
        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/accounts?search={username}");
        foreach (var pair in new[] { detail, list.GetProperty("items")[0] }.Select(a => Assert.Single(a.GetProperty("pairs").EnumerateArray())))
        {
            Assert.Equal(zone.Id, pair.GetProperty("zoneId").GetGuid());
            Assert.Equal(zone.Name, pair.GetProperty("zoneName").GetString());
            Assert.False(pair.GetProperty("allZones").GetBoolean());
            Assert.Equal(FixedRoles.AccountantCode, pair.GetProperty("role").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = temporaryPassword })).StatusCode);
    }

    // A pair sent with the account is in the answer of the create, and only Effective pairs are listed.
    [Fact]
    public async Task A_create_with_pairs_answers_them_and_a_revoked_pair_leaves_the_list()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var role = await factory.RoleAsync(StaffRole.ZoneManager);

        var created = await CreateStaffAsync(owner, new[] { new { zoneId = (Guid?)null, roleId = role.Id } });
        var account = created.GetProperty("account");
        var pair = Assert.Single(account.GetProperty("pairs").EnumerateArray());
        Assert.True(pair.GetProperty("allZones").GetBoolean());

        var id = account.GetProperty("id").GetGuid();
        var revoked = await owner.PostAsJsonAsync($"/api/accounts/{id}/grants/{pair.GetProperty("id").GetGuid()}/revoke", new { reason = "Moved" });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}")).GetProperty("pairs").EnumerateArray());
    }

    // LABOR-01 creates the staff account through the same rule: no pair needed, and the same field check on the email.
    [Fact]
    public async Task An_employee_account_is_created_without_a_pair_and_a_taken_email_is_refused()
    {
        var email = $"taken-{NewCode()}@example.com";
        var holder = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.Email = email);
        await factory.EnsureCooperativeAsync();
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var zone = await factory.AddZoneAsync(NewCode());

        var saved = await owner.PostAsJsonAsync("/api/employees", EmployeeBody(zone, new { username = $"nv{NewCode()}" }));
        var taken = await owner.PostAsJsonAsync("/api/employees", EmployeeBody(zone, new { username = $"nv{NewCode()}", email }));

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.NotEmpty((await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString()!);
        Assert.Equal("MSG-ACCOUNT-09", await taken.ErrorCodeAsync());
        Assert.Equal(holder.Id, (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingId").GetGuid());
    }

    // ACCOUNT-09.4: only an Active account gets a temporary password; its password and sessions stay as they were.
    [Fact]
    public async Task A_locked_account_gets_no_temporary_password()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var locked = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.StatusId = StatusIds.Account.Locked);

        var refused = await owner.PostAsync($"/api/accounts/{locked.Id}/reset-password", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("MSG-GEN-09", await refused.ErrorCodeAsync());
        Assert.Equal("Account is Locked and cannot be used for this action.", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        var stored = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == locked.Id));
        Assert.False(stored.MustChangePassword);
        Assert.Equal(locked.PasswordHash, stored.PasswordHash);
    }

    private static async Task<JsonElement> CreateStaffAsync(HttpClient owner, object? grants, string? phone = null)
    {
        var response = await owner.PostAsJsonAsync("/api/accounts", StaffBody(phone, grants: grants));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object StaffBody(string? phone = null, string? email = null, object? grants = null) => new
    {
        displayName = "Nguyễn Văn A",
        username = $"s{NewCode()}",
        phoneNumber = phone ?? NewPhone(),
        email,
        grants,
    };

    private static Dictionary<string, object?> EmployeeBody(FarmingZone zone, object account) =>
        Employees.EmployeeRequests.Profile($"Nhân viên {NewCode()}", NewPhone(), zone.Id, body => body["account"] = account);
}
