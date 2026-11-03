using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>ACS-09 – the Owner's management of staff accounts.</summary>
public class StaffAccountTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Owner_creates_a_staff_account_whose_temporary_password_must_be_changed()
    {
        var (owner, zone, role) = await SetupAsync();

        var created = await CreateAsync(owner, zone, role);
        var temporaryPassword = created.GetProperty("temporaryPassword").GetString()!;
        var username = created.GetProperty("account").GetProperty("username").GetString()!;
        var tokens = await LoginTokensAsync(factory.CreateClient(), username, temporaryPassword);

        Assert.Equal(10, temporaryPassword.Length);
        Assert.True(tokens.GetProperty("mustChangePassword").GetBoolean());
        Assert.True(tokens.GetProperty("phoneVerificationRequired").GetBoolean());
        var audit = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordCode == username).ToListAsync());
        Assert.Contains(audit, e => e.Action == "Account.Created");
        Assert.All(audit, e => Assert.DoesNotContain(temporaryPassword, (e.Before ?? "") + (e.After ?? "")));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains(temporaryPassword));
    }

    [Fact]
    public async Task Only_the_owner_manages_accounts()
    {
        var (_, zone, role) = await SetupAsync();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, await factory.AddRoleAsync($"All {NewCode()}", (PermissionFunctions.WaterMeasurements, true, true, true)), null);
        var client = await factory.LoginAsync(staff.Username);

        var list = await client.GetAsync("/api/accounts");
        var create = await client.PostAsJsonAsync("/api/accounts", Body(zone, role));

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal("MSG-GEN-08", await create.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/accounts")).StatusCode);
    }

    [Fact]
    public async Task Owner_and_trader_accounts_are_out_of_reach_and_owner_rights_are_refused()
    {
        var (owner, zone, role) = await SetupAsync();
        var otherOwner = await factory.AddAccountAsync(NewCode(), Password);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);

        var lockOwner = await owner.PostAsJsonAsync($"/api/accounts/{otherOwner.Id}/lock", new { reason = "test" });
        var resetTrader = await owner.PostAsync($"/api/accounts/{trader.Id}/reset-password", null);
        var lockTrader = await owner.PostAsJsonAsync($"/api/accounts/{trader.Id}/lock", new { reason = "test" });
        var ownerRights = await owner.PostAsJsonAsync("/api/accounts", Body(zone, role, subjectType: "CooperativeOwner"));

        Assert.Equal("MSG-GEN-08", await lockOwner.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await resetTrader.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await lockTrader.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await ownerRights.ErrorCodeAsync());
        Assert.True(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Account.OwnerRightsRefused")));
    }

    [Fact]
    public async Task Duplicates_and_missing_rows_are_refused()
    {
        var (owner, zone, role) = await SetupAsync();
        var existing = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var duplicateUsername = await owner.PostAsJsonAsync("/api/accounts", Body(zone, role, username: existing.Username));
        var duplicatePhone = await owner.PostAsJsonAsync("/api/accounts", Body(zone, role, phone: existing.PhoneNumber));
        var noGrant = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "A", username = $"s{NewCode()}", phoneNumber = NewPhone(), grants = Array.Empty<object>() });
        var phoneLikeUsername = await owner.PostAsJsonAsync("/api/accounts", Body(zone, role, username: "0912345678"));

        Assert.Equal("MSG-ACCOUNT-09", await duplicateUsername.ErrorCodeAsync());
        Assert.Equal(existing.Id, (await duplicateUsername.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingId").GetGuid());
        Assert.Equal("MSG-ACCOUNT-09", await duplicatePhone.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await noGrant.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await phoneLikeUsername.ErrorCodeAsync());
    }

    [Fact]
    public async Task Lock_ends_every_session_and_cannot_be_repeated()
    {
        var (owner, zone, role) = await SetupAsync();
        var created = await CreateAsync(owner, zone, role);
        var id = created.GetProperty("account").GetProperty("id").GetGuid();
        var staffSession = factory.CreateClient();
        await LoginTokensAsync(staffSession, created.GetProperty("account").GetProperty("username").GetString()!, created.GetProperty("temporaryPassword").GetString()!);

        var noReason = await owner.PostAsJsonAsync($"/api/accounts/{id}/lock", new { reason = "" });
        var locked = await owner.PostAsJsonAsync($"/api/accounts/{id}/lock", new { reason = "Left the cooperative" });
        var again = await owner.PostAsJsonAsync($"/api/accounts/{id}/lock", new { reason = "Again" });
        var unlocked = await owner.PostAsJsonAsync($"/api/accounts/{id}/unlock", new { reason = "Back" });

        Assert.Equal("MSG-GEN-01", await noReason.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal("MSG-GEN-09", await again.ErrorCodeAsync());
        Assert.Equal("Account is Locked and cannot be used for this action.",
            (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.False(await factory.QueryAsync(db => db.LoginSessions.AnyAsync(s => s.AccountId == id && s.RevokedAt == null)));
        Assert.True(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordId == id && e.Action == "Account.StatusChanged" && e.Reason == "Left the cooperative")));
    }

    [Fact]
    public async Task Reset_issues_a_new_temporary_password_that_expires_after_24_hours()
    {
        var (owner, zone, role) = await SetupAsync();
        var created = await CreateAsync(owner, zone, role);
        var id = created.GetProperty("account").GetProperty("id").GetGuid();
        var username = created.GetProperty("account").GetProperty("username").GetString()!;
        var session = await factory.LoginAsync(username, created.GetProperty("temporaryPassword").GetString()!);

        var reset = await (await owner.PostAsync($"/api/accounts/{id}/reset-password", null)).Content.ReadFromJsonAsync<JsonElement>();
        var newPassword = reset.GetProperty("temporaryPassword").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = newPassword })).StatusCode);

        await factory.UpdateAsync<Account>(id, a => a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddHours(-25));
        var expired = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = newPassword });
        Assert.Equal("MSG-ACCOUNT-08", await expired.ErrorCodeAsync());
    }

    [Fact]
    public async Task Editing_phone_clears_its_verification_and_a_stale_version_is_refused()
    {
        var (owner, zone, role) = await SetupAsync();
        var created = await CreateAsync(owner, zone, role);
        var account = created.GetProperty("account");
        var id = account.GetProperty("id").GetGuid();
        await factory.UpdateAsync<Account>(id, a => a.PhoneVerifiedAt = DateTimeOffset.UtcNow);
        var current = await owner.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}");

        var stale = await owner.PutAsJsonAsync($"/api/accounts/{id}", new { displayName = "B", username = account.GetProperty("username").GetString(), phoneNumber = NewPhone(), rowVersion = "AAAAAAAAB9E=" });
        var saved = await owner.PutAsJsonAsync($"/api/accounts/{id}", new { displayName = "B", username = account.GetProperty("username").GetString(), phoneNumber = NewPhone(), rowVersion = current.GetProperty("rowVersion").GetString() });

        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.False((await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("phoneVerified").GetBoolean());
    }

    private async Task<(HttpClient Owner, FarmingZone Zone, Role Role)> SetupAsync()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.AddRoleAsync($"Zone role {NewCode()}", (PermissionFunctions.WaterMeasurements, true, true, false));
        return (await factory.LoginAsync(owner.Username), zone, role);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient owner, FarmingZone zone, Role role)
    {
        var response = await owner.PostAsJsonAsync("/api/accounts", Body(zone, role));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Body(FarmingZone zone, Role role, string? username = null, string? phone = null, string? subjectType = null) => new
    {
        displayName = "Nguyễn Văn A",
        username = username ?? $"s{NewCode()}",
        phoneNumber = phone ?? NewPhone(),
        grants = new[] { new { zoneId = zone.Id, roleId = role.Id } },
        subjectType,
    };
}
