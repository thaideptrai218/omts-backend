using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// GBR-02, NFR-01: passwords, temporary passwords, verification codes and tokens are never written to the audit log, and
/// the search never shows one, also for a row written with one by hand.
/// </summary>
public class AuditSecretScanTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task The_account_flows_write_no_secret_and_the_search_shows_none()
    {
        var (_, owner) = await factory.OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.RoleAsync(StaffRole.ZoneManager);
        var (username, phone) = ($"s{NewCode()}", NewPhone());
        var secrets = new List<string>();

        // ACCOUNT-09: create the staff account, then issue a new temporary password.
        var created = await ReadAsync(await owner.PostAsJsonAsync("/api/accounts", new
        {
            displayName = "Nguyễn Văn Kiểm",
            username,
            phoneNumber = phone,
            grants = new[] { new { zoneId = zone.Id, roleId = role.Id } },
        }), HttpStatusCode.Created);
        var staffId = created.GetProperty("account").GetProperty("id").GetGuid();
        secrets.Add(created.GetProperty("temporaryPassword").GetString()!);
        var temporary = (await ReadAsync(await owner.PostAsync($"/api/accounts/{staffId}/reset-password", null))).GetProperty("temporaryPassword").GetString()!;
        secrets.Add(temporary);

        // ACCOUNT-02 and ACCOUNT-04: log in with the temporary password and change it.
        var staff = factory.CreateClient();
        var tokens = await LoginTokensAsync(staff, username, temporary);
        AddTokens(secrets, tokens);
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var changedPassword = NewPassword();
        secrets.Add(changedPassword);
        var changed = await ReadAsync(await staff.PostAsJsonAsync("/api/me/password", new { currentPassword = temporary, newPassword = changedPassword, passwordConfirmation = changedPassword }));
        AddTokens(secrets, changed.GetProperty("tokens"));
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.GetProperty("tokens").GetProperty("accessToken").GetString());

        // ACCOUNT-02.1: verify the phone number (SMS code, verification token).
        var verificationId = await VerificationIdAsync(await staff.PostAsJsonAsync("/api/me/phone-verification", new { }));
        secrets.Add(factory.LastSmsCode(phone));
        var phoneToken = await VerifyAsync(staff, verificationId, factory.LastSmsCode(phone));
        secrets.Add(phoneToken);
        await ReadAsync(await staff.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = phoneToken }));

        // ACCOUNT-03: recover the password with an SMS code.
        var anonymous = factory.CreateClient();
        var recovery = await ReadAsync(await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier = phone }), HttpStatusCode.Accepted);
        secrets.Add(factory.LastSmsCode(phone));
        var recoveryToken = await VerifyAsync(anonymous, recovery.GetProperty("requestId").GetGuid(), factory.LastSmsCode(phone));
        secrets.Add(recoveryToken);
        var recoveredPassword = NewPassword();
        secrets.Add(recoveredPassword);
        await ReadAsync(await anonymous.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = recoveryToken, newPassword = recoveredPassword, passwordConfirmation = recoveredPassword }));
        AddTokens(secrets, await LoginTokensAsync(factory.CreateClient(), username, recoveredPassword));

        // The stored hashes are secrets too.
        secrets.Add(await factory.QueryAsync(db => db.Accounts.Where(a => a.Id == staffId).Select(a => a.PasswordHash).SingleAsync()));
        secrets.AddRange(await factory.QueryAsync(db => db.VerificationCodes.Where(c => c.Target == phone).Select(c => c.CodeHash).ToListAsync()));
        secrets.AddRange(await factory.QueryAsync(db => db.LoginSessions.Where(s => s.AccountId == staffId).Select(s => s.RefreshTokenHash).ToListAsync()));
        Assert.All(secrets, s => Assert.False(string.IsNullOrEmpty(s)));

        var stored = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == staffId || e.ActorAccountId == staffId).ToListAsync());
        Assert.Superset(
            new HashSet<string> { "Account.Created", "Account.PasswordReset", "Account.PasswordChanged", "Account.PhoneVerified" },
            stored.Select(e => e.Action).ToHashSet());
        Assert.Equal(2, stored.Count(e => e.Action == "Account.PasswordReset"));
        foreach (var e in stored)
        {
            AssertNoSecret(secrets, $"{e.Action}: {e.Before} {e.After} {e.Reason} {e.RecordCode}");
        }

        foreach (var query in new[] { $"recordCode={username}", $"actorAccountId={staffId}" })
        {
            var list = await owner.GetStringAsync($"/api/audit-events?{query}");
            AssertNoSecret(secrets, list);
            foreach (var item in JsonDocument.Parse(list).RootElement.GetProperty("page").GetProperty("items").EnumerateArray())
            {
                AssertNoSecret(secrets, await owner.GetStringAsync($"/api/audit-events/{item.GetProperty("id").GetGuid()}"));
            }
        }
    }

    // Defence in depth: a row written by an older version or by hand is cleaned again when it is shown.
    [Fact]
    public async Task A_stored_secret_is_removed_from_the_values_shown()
    {
        var (_, client) = await factory.OwnerAsync();
        var (password, token, code) = (NewPassword(), NewPassword(), NewPassword());
        var e = Event($"{NewTag()}-1", DateTimeOffset.UtcNow, "Account.Updated", "Account");
        e.Before = JsonSerializer.Serialize(new { DisplayName = "Lê Văn Cũ", Password = password, Contact = new { PhoneNumber = "0900000001", OtpCode = code } });
        e.After = JsonSerializer.Serialize(new { DisplayName = "Lê Văn Mới", Sessions = new[] { new { Device = "web", RefreshToken = token } }, MustChangePassword = true });
        await factory.SeedAsync(e);

        var response = await client.GetStringAsync($"/api/audit-events/{e.Id}");

        AssertNoSecret([password, token, code], response);
        var detail = JsonDocument.Parse(response).RootElement;
        Assert.Equal(["DisplayName", "Contact"], detail.GetProperty("before").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["PhoneNumber"], detail.GetProperty("before").GetProperty("Contact").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["Device"], detail.GetProperty("after").GetProperty("Sessions")[0].EnumerateObject().Select(p => p.Name));
        // A flag about a password is not a secret.
        Assert.True(detail.GetProperty("after").GetProperty("MustChangePassword").GetBoolean());
    }

    private static void AssertNoSecret(IEnumerable<string> secrets, string text)
    {
        foreach (var secret in secrets)
        {
            // A 6-digit code is looked for as a number of its own, not inside a time or a phone number.
            var found = secret.All(char.IsAsciiDigit)
                ? Regex.IsMatch(text, $@"(?<!\d){secret}(?!\d)")
                : text.Contains(secret, StringComparison.Ordinal);
            Assert.False(found, $"A secret ({secret.Length} characters) appears in: {text}");
        }
    }

    private static void AddTokens(List<string> secrets, JsonElement tokens)
    {
        secrets.Add(tokens.GetProperty("accessToken").GetString()!);
        secrets.Add(tokens.GetProperty("refreshToken").GetString()!);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
