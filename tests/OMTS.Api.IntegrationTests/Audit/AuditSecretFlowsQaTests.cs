using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// QA of OMTS-216 PR 0.6 (GBR-02, NFR-01): the audit-writing flows the first secret scan did not run, ACCOUNT-01 by phone
/// and by email, ACCOUNT-05.1 email and phone changes, ACCOUNT-04.2 (code by email) and a push device moving to another
/// account, write no password, code, token or hash, and no ACCOUNT-10 answer shows one.
/// </summary>
public class AuditSecretFlowsQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Registration_contact_changes_password_by_email_code_and_device_moves_write_and_show_no_secret()
    {
        await factory.EnsureCooperativeAsync();
        var (ownerAccount, owner) = await factory.OwnerAsync();
        var secrets = new List<string>();

        var (phone, phonePassword) = (NewPhone(), NewPassword());
        var traderId = await RegisterAsync("Sms", phone, phonePassword, secrets);
        var registrationEmail = $"r{NewCode()}@example.com";
        var emailTraderId = await RegisterAsync("Email", registrationEmail, NewPassword(), secrets);

        var trader = factory.CreateClient();
        var tokens = await LoginTokensAsync(trader, phone, phonePassword);
        UseTokens(trader, tokens, secrets);

        // ACCOUNT-05.1: add an email.
        var email = $"t{NewCode()}@example.com";
        var emailChange = await VerificationIdAsync(await trader.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = phonePassword }));
        var emailToken = await VerifyAsync(trader, emailChange, Keep(secrets, factory.LastEmailCode(email)));
        await ReadAsync(await trader.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = Keep(secrets, emailToken) }));

        // ACCOUNT-04.2: the current password is forgotten, a code goes to the email.
        var passwordCode = await VerificationIdAsync(await trader.PostAsync("/api/me/password/code", null));
        var passwordToken = await VerifyAsync(trader, passwordCode, Keep(secrets, factory.LastEmailCode(email)));
        var newPassword = Keep(secrets, NewPassword());
        var changed = await ReadAsync(await trader.PostAsJsonAsync("/api/me/password",
            new { verificationToken = Keep(secrets, passwordToken), newPassword, passwordConfirmation = newPassword }));
        UseTokens(trader, changed.GetProperty("tokens"), secrets);

        // ACCOUNT-05.1: change the phone number.
        var newPhone = NewPhone();
        var phoneChange = await VerificationIdAsync(await trader.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = newPhone, currentPassword = newPassword }));
        var phoneToken = await VerifyAsync(trader, phoneChange, Keep(secrets, factory.LastSmsCode(newPhone)));
        await ReadAsync(await trader.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = Keep(secrets, phoneToken) }));

        // The device logs in with the Owner account: its push token moves (PushDevice.Moved).
        var pushToken = Keep(secrets, $"ExponentPushToken[{NewCode()}{NewCode()}]");
        await ReadAsync(await trader.PostAsJsonAsync("/api/me/devices", new { token = pushToken, platform = "android", permissionGranted = true }), HttpStatusCode.NoContent, HttpStatusCode.OK);
        await ReadAsync(await owner.PostAsJsonAsync("/api/me/devices", new { token = pushToken, platform = "android", permissionGranted = true }), HttpStatusCode.NoContent, HttpStatusCode.OK);

        Guid[] accounts = [traderId, emailTraderId];
        string[] targets = [phone, registrationEmail, email, newPhone];
        secrets.AddRange(await factory.QueryAsync(db => db.Accounts.Where(a => accounts.Contains(a.Id)).Select(a => a.PasswordHash).ToListAsync()));
        secrets.AddRange(await factory.QueryAsync(db => db.VerificationCodes.Where(c => targets.Contains(c.Target)).Select(c => c.CodeHash).ToListAsync()));
        secrets.AddRange((await factory.QueryAsync(db => db.VerificationCodes.Where(c => targets.Contains(c.Target)).Select(c => c.TokenHash).ToListAsync())).OfType<string>());
        secrets.AddRange(await factory.QueryAsync(db => db.LoginSessions.Where(s => accounts.Contains(s.AccountId)).Select(s => s.RefreshTokenHash).ToListAsync()));
        Assert.All(secrets, s => Assert.False(string.IsNullOrEmpty(s)));

        var deviceId = await factory.QueryAsync(db => db.PushDevices.Where(d => d.Token == pushToken).Select(d => d.Id).SingleAsync());
        Guid?[] records = [traderId, emailTraderId, deviceId];
        Guid?[] actors = [traderId, emailTraderId];
        var stored = await factory.QueryAsync(db => db.AuditEvents
            .Where(e => records.Contains(e.RecordId) || actors.Contains(e.ActorAccountId)).ToListAsync());
        Assert.Superset(
            new HashSet<string> { "Account.Registered", "Account.ContactChanged", "Account.PasswordChanged", "PushDevice.Moved" },
            stored.Select(e => e.Action).ToHashSet());
        Assert.Equal(2, stored.Count(e => e.Action == "Account.Registered"));
        Assert.Equal(2, stored.Count(e => e.Action == "Account.ContactChanged"));
        foreach (var e in stored)
        {
            AssertNoSecret(secrets, $"{e.Action}: {e.Before} {e.After} {e.Reason} {e.RecordCode}");
        }

        var answers = new List<string> { await owner.GetStringAsync("/api/audit-events/facets") };
        foreach (var query in new[] { $"actorAccountId={traderId}", $"actorAccountId={emailTraderId}", $"actorAccountId={ownerAccount.Id}&action=PushDevice.Moved" })
        {
            var list = await owner.GetStringAsync($"/api/audit-events?{query}");
            answers.Add(list);
            foreach (var item in JsonDocument.Parse(list).RootElement.GetProperty("page").GetProperty("items").EnumerateArray())
            {
                answers.Add(await owner.GetStringAsync($"/api/audit-events/{item.GetProperty("id").GetGuid()}"));
                answers.Add(await (await owner.GetAsync($"/api/audit-events/{item.GetProperty("id").GetGuid()}/record")).Content.ReadAsStringAsync());
            }
        }

        Assert.True(answers.Count > 10, $"Only {answers.Count} answers read.");
        Assert.All(answers, a => AssertNoSecret(secrets, a));
    }

    /// <summary>
    /// A secret looked for as text; a code of digits only as a number of its own, not as a part of a GUID, a time or a
    /// phone number.
    /// </summary>
    internal static void AssertNoSecret(IEnumerable<string> secrets, string text)
    {
        foreach (var secret in secrets)
        {
            var found = secret.All(char.IsAsciiDigit)
                ? Regex.IsMatch(text, $@"(?<![0-9A-Fa-f]){secret}(?![0-9A-Fa-f])")
                : text.Contains(secret, StringComparison.Ordinal);
            Assert.False(found, $"A secret ({secret.Length} characters) appears in: {text}");
        }
    }

    private async Task<Guid> RegisterAsync(string channel, string identifier, string password, List<string> secrets)
    {
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel, target = identifier }));
        var code = Keep(secrets, channel == "Sms" ? factory.LastSmsCode(identifier) : factory.LastEmailCode(identifier));
        var token = Keep(secrets, await VerifyAsync(client, id, code));
        Keep(secrets, password);
        var registered = await ReadAsync(await client.PostAsJsonAsync("/api/auth/register",
            new { channel, identifier, password, passwordConfirmation = password, verificationToken = token }), HttpStatusCode.Created);
        return registered.GetProperty("accountId").GetGuid();
    }

    private static string Keep(List<string> secrets, string secret)
    {
        secrets.Add(secret);
        return secret;
    }

    private static void UseTokens(HttpClient client, JsonElement tokens, List<string> secrets)
    {
        secrets.Add(tokens.GetProperty("accessToken").GetString()!);
        secrets.Add(tokens.GetProperty("refreshToken").GetString()!);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, params HttpStatusCode[] expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        var accepted = expected.Length == 0 ? new[] { HttpStatusCode.OK } : expected;
        Assert.True(accepted.Contains(response.StatusCode), $"{(int)response.StatusCode} {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }
}
