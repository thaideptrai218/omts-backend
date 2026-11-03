using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Verification;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// ACS-03 opacity and ACS-06 abuse limits, end to end: recovery cannot be told apart by its id, the send and
/// attempt budgets hold across requests and channels, and no code, token or password reaches logs or audit.
/// </summary>
public class VerificationAbuseTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string TraderPassword = "Trader2026";
    private static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    // A resend or verify on a recovery id must answer exactly like one on an id that was never issued.
    [Fact]
    public async Task Recovery_resend_and_verify_answer_the_same_for_real_and_random_ids()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var anonymous = factory.CreateClient();
        var real = await RecoveryRequestIdAsync(anonymous, owner.PhoneNumber!);
        var random = Guid.NewGuid();
        var wrong = "000000";

        var realResend = await anonymous.PostAsync($"/api/verifications/{real}/resend", null);
        var randomResend = await anonymous.PostAsync($"/api/verifications/{random}/resend", null);
        var realVerify = await anonymous.PostAsJsonAsync($"/api/verifications/{real}/verify", new { code = wrong });
        var randomVerify = await anonymous.PostAsJsonAsync($"/api/verifications/{random}/verify", new { code = wrong });

        Assert.Equal(await Shape(randomResend), await Shape(realResend));
        Assert.Equal(await Shape(randomVerify), await Shape(realVerify));
    }

    // An unknown, unverified or locked identifier gets the same 202 body as a real one, and no code is sent to it.
    [Fact]
    public async Task Recovery_start_answers_the_same_for_unknown_unverified_and_locked_identifiers()
    {
        var unverifiedPhone = NewPhone();
        await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.PhoneNumber = unverifiedPhone; a.PhoneVerifiedAt = null; });
        var lockedPhone = NewPhone();
        await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.PhoneNumber = lockedPhone; a.StatusId = StatusIds.Account.Locked; });
        var real = await factory.AddAccountAsync(NewCode(), Password);
        var anonymous = factory.CreateClient();

        var responses = new List<HttpResponseMessage>
        {
            await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier = real.PhoneNumber }),
            await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier = NewPhone() }),
            await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier = unverifiedPhone }),
            await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier = lockedPhone }),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var bodies = new List<JsonElement>();
        foreach (var response in responses)
        {
            bodies.Add(await response.Content.ReadFromJsonAsync<JsonElement>());
        }

        Assert.All(bodies, b => Assert.Equal(bodies[0].GetProperty("message").GetString(), b.GetProperty("message").GetString()));
        Assert.All(bodies, b => Assert.Equal(bodies[0].EnumerateObject().Select(p => p.Name), b.EnumerateObject().Select(p => p.Name)));
        Assert.All(bodies, b => Assert.NotEqual(Guid.Empty, b.GetProperty("requestId").GetGuid()));
        Assert.NotEmpty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(real.PhoneNumber!));
        Assert.Empty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(unverifiedPhone));
        Assert.Empty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(lockedPhone));
    }

    // Within one minute, a second start on the other channel is refused: nothing is sent and the answer is the same 202.
    [Fact]
    public async Task Alternating_channel_recovery_within_a_minute_sends_only_one_code()
    {
        var (owner, email) = await OwnerWithPhoneAndEmailAsync();
        var anonymous = factory.CreateClient();

        await RecoveryRequestIdAsync(anonymous, owner.PhoneNumber!);
        await RecoveryRequestIdAsync(anonymous, email);

        Assert.Single(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(owner.PhoneNumber!));
        Assert.Empty(factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List(email));
    }

    // Phone and email alternate, one minute apart: the account's budget of 5 requests per hour holds across channels.
    [Fact]
    public async Task Alternating_phone_and_email_recovery_starts_stop_at_the_per_account_budget()
    {
        var (owner, email) = await OwnerWithPhoneAndEmailAsync();
        var anonymous = factory.CreateClient();

        for (var i = 0; i < 8; i++)
        {
            var identifier = i % 2 == 0 ? owner.PhoneNumber! : email;
            var response = await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await AgeRequestsOfAsync(owner.Id);
        }

        var sms = factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(owner.PhoneNumber!).Count;
        var mail = factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List(email).Count;
        Assert.Equal(VerificationService.MaxRequestsPerWindow, sms + mail);
    }

    // Starting a new request cancels the open one, so failures count across requests: 4 wrong on the first, 5 on the
    // second (which locks), 1 on the third. Ten failures in the hour: no new code is sent, and a right code on the
    // open third request is refused with MSG69.
    [Fact]
    public async Task Ten_wrong_entries_across_requests_refuse_the_right_code_with_msg_account_17()
    {
        var (owner, _) = await OwnerWithPhoneAndEmailAsync();
        var anonymous = factory.CreateClient();
        var phone = owner.PhoneNumber!;

        var first = await RecoveryRequestIdAsync(anonymous, phone);
        var firstCode = factory.LastSmsCode(phone);
        await WrongEntriesAsync(anonymous, first, firstCode, 4);
        await AgeRequestsOfAsync(owner.Id);

        var second = await RecoveryRequestIdAsync(anonymous, phone);
        var secondCode = factory.LastSmsCode(phone);
        await WrongEntriesAsync(anonymous, second, secondCode, VerificationCode.MaxFailedAttempts);
        await AgeRequestsOfAsync(owner.Id);

        var third = await RecoveryRequestIdAsync(anonymous, phone);
        var thirdCode = factory.LastSmsCode(phone);
        await WrongEntriesAsync(anonymous, third, thirdCode, 1);

        var sent = factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone).Count;
        await AgeRequestsOfAsync(owner.Id);
        var fourth = await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier = phone });
        Assert.Equal(HttpStatusCode.Accepted, fourth.StatusCode);
        Assert.Equal(sent, factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone).Count);

        var right = await anonymous.PostAsJsonAsync($"/api/verifications/{third}/verify", new { code = thirdCode });
        Assert.Equal("MSG-ACCOUNT-17", await right.ErrorCodeAsync());
        Assert.False((await right.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("verificationToken", out _));
    }

    private static async Task WrongEntriesAsync(HttpClient anonymous, Guid verificationId, string code, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await anonymous.PostAsJsonAsync($"/api/verifications/{verificationId}/verify", new { code = WrongCodeFor(code) });
            Assert.Equal("MSG-ACCOUNT-16", await response.ErrorCodeAsync());
        }
    }

    // Every secret of registration, recovery (phone and email), staff creation and password changes stays out of
    // the logs and the audit trail. The flows must succeed, or the check proves nothing.
    [Fact]
    public async Task Codes_tokens_and_passwords_never_reach_the_logs_or_the_audit_trail()
    {
        await factory.EnsureCooperativeAsync();
        var anonymous = factory.CreateClient();
        var tokens = new List<string>();

        // Registration (ACS-01) by SMS.
        var traderPhone = NewPhone();
        var registrationToken = await RegistrationTokenAsync(anonymous, traderPhone, tokens);
        var registered = await anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            channel = "Sms",
            identifier = traderPhone,
            password = TraderPassword,
            passwordConfirmation = TraderPassword,
            verificationToken = registrationToken,
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        // Recovery (ACS-03) by SMS, then by email.
        var (owner, email) = await OwnerWithPhoneAndEmailAsync();
        await ResetThroughRecoveryAsync(anonymous, owner.PhoneNumber!, "Reset2026x", tokens);
        var ownerEmail = await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.Email = $"by-mail-{NewCode()}@example.com"; a.EmailVerifiedAt = DateTimeOffset.UtcNow; });
        await ResetThroughRecoveryAsync(anonymous, ownerEmail.Email!, "Reset2027x", tokens);

        // Staff creation (ACS-09): the temporary password is returned once, never logged.
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.AddRoleAsync($"Zone role {NewCode()}", (PermissionFunctions.WaterMeasurements, true, true, false));
        // The owner's password was just reset by recovery.
        var ownerClient = await factory.LoginAsync(owner.Username, "Reset2026x");
        var username = $"s{NewCode()}";
        var created = await ownerClient.PostAsJsonAsync("/api/accounts", new
        {
            displayName = "Nguyễn Văn C",
            username,
            phoneNumber = NewPhone(),
            grants = new[] { new { zoneId = zone.Id, roleId = role.Id } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var temporaryPassword = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString()!;

        // Password changes (ACS-04): a wrong current password, the staff's first change, and the owner's change.
        var staff = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = temporaryPassword });
        var staffTokens = (await staff.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        var staffClient = factory.CreateClient();
        staffClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", staffTokens);
        var staffChanged = await staffClient.PostAsJsonAsync("/api/me/password", new { currentPassword = temporaryPassword, newPassword = "Staff2026x", passwordConfirmation = "Staff2026x" });
        var wrongCurrent = await ownerClient.PostAsJsonAsync("/api/me/password", new { currentPassword = "Wrong123", newPassword = "Owner2026x", passwordConfirmation = "Owner2026x" });
        var ownerChanged = await ownerClient.PostAsJsonAsync("/api/me/password", new { currentPassword = "Reset2026x", newPassword = "Owner2026y", passwordConfirmation = "Owner2026y" });
        Assert.Equal(HttpStatusCode.OK, staffChanged.StatusCode);
        Assert.Equal("MSG-ACCOUNT-14", await wrongCurrent.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, ownerChanged.StatusCode);

        var secrets = new List<(string Kind, string Value, bool IsCode)>();
        secrets.AddRange(SentCodes().Select(code => ("verification code", code, true)));
        secrets.AddRange(tokens.Select(token => ("verification token", token, false)));
        secrets.AddRange(new[]
        {
            ("temporary password", temporaryPassword, false),
            ("password", TraderPassword, false),
            ("password", "Reset2026x", false),
            ("password", "Reset2027x", false),
            ("password", "Staff2026x", false),
            ("password", "Owner2026x", false),
            ("password", "Owner2026y", false),
            ("password", "Wrong123", false),
            ("password", Password, false),
        });

        var logs = factory.Logs.Lines.ToList();
        var audit = await factory.QueryAsync(db => db.AuditEvents.ToListAsync());
        var auditText = audit.Select(e => JsonSerializer.Serialize(e)).ToList();

        Assert.NotEmpty(logs);
        Assert.Contains(audit, e => e.Action == "Account.Registered");
        Assert.Contains(audit, e => e.Action == "Account.Created");
        Assert.Contains(audit, e => e.Action == "Account.PasswordReset");
        Assert.DoesNotContain(logs, line => secrets.Any(s => Leaks(line, s.Value, s.IsCode)));
        Assert.DoesNotContain(auditText, text => secrets.Any(s => Leaks(text, s.Value, s.IsCode)));
    }

    // ---- helpers ----

    /// <summary>Owner with a verified phone and a verified email (the recovery identifiers), and the email.</summary>
    private async Task<(Account Owner, string Email)> OwnerWithPhoneAndEmailAsync()
    {
        var email = $"owner-{NewCode()}@example.com";
        var owner = await factory.AddAccountAsync(NewCode(), Password, configure: a =>
        {
            a.Email = email;
            a.EmailVerifiedAt = DateTimeOffset.UtcNow;
        });
        return (owner, email);
    }

    private async Task<Guid> RecoveryRequestIdAsync(HttpClient anonymous, string identifier)
    {
        var response = await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetGuid();
    }

    /// <summary>Starts a recovery, enters the right code, and sets a new password with the token.</summary>
    private async Task ResetThroughRecoveryAsync(HttpClient anonymous, string identifier, string newPassword, List<string> tokens)
    {
        var requestId = await RecoveryRequestIdAsync(anonymous, identifier);
        var code = Regex.IsMatch(identifier, @"^[^@]+@") ? factory.LastEmailCode(identifier) : factory.LastSmsCode(identifier);
        var token = await VerifiedTokenAsync(anonymous, requestId, code, tokens);
        var reset = await anonymous.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = token, newPassword, passwordConfirmation = newPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }

    /// <summary>Starts a registration for the number and reads the SMS code only after it was sent.</summary>
    private async Task<string> RegistrationTokenAsync(HttpClient anonymous, string phone, List<string> tokens)
    {
        var id = await VerificationIdAsync(await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));
        return await VerifiedTokenAsync(anonymous, id, factory.LastSmsCode(phone), tokens);
    }

    private static async Task<string> VerifiedTokenAsync(HttpClient anonymous, Guid verificationId, string code, List<string> tokens)
    {
        var token = await VerifyAsync(anonymous, verificationId, code);
        tokens.Add(token);
        return token;
    }

    /// <summary>Moves the account's requests two minutes into the past, as if that much time had passed.</summary>
    private async Task AgeRequestsOfAsync(Guid accountId)
    {
        var ids = await factory.QueryAsync(db => db.VerificationCodes.Where(c => c.AccountId == accountId).Select(c => c.Id).ToListAsync());
        foreach (var id in ids)
        {
            await factory.UpdateAsync<VerificationCode>(id, c =>
            {
                c.CreatedAt -= TwoMinutes;
                c.LastSentAt -= TwoMinutes;
            });
        }
    }

    /// <summary>Every code the fake SMS and email senders delivered, read from their dev inboxes.</summary>
    private IEnumerable<string> SentCodes() =>
        factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List().Select(m => Regex.Match(m.Text, @"\d{6}").Value)
            .Concat(factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List().Select(m => Regex.Match(m.Text, @"\d{6}").Value))
            .Where(code => code.Length == 6)
            .Distinct();

    private static string WrongCodeFor(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    /// <summary>Codes are matched as whole numbers so a longer digit run does not count; other secrets as plain text.</summary>
    private static bool Leaks(string text, string secret, bool isCode) =>
        isCode ? Regex.IsMatch(text, $@"(?<!\d){secret}(?!\d)") : text.Contains(secret, StringComparison.Ordinal);

    /// <summary>Status, MSG code and property names of a problem response; the same shape means no hint.</summary>
    private static async Task<string> Shape(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = body.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal);
        return $"{(int)response.StatusCode}|{body.GetProperty("code").GetString()}|{string.Join(",", keys)}";
    }
}
