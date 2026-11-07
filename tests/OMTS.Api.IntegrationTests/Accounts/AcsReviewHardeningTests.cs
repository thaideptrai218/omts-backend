using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>Review follow-ups of OMTS-198: account takeover through contacts, SMS cost limits, stale forms, roles.</summary>
public class AcsReviewHardeningTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // A stolen session alone cannot replace the contacts (and then reset the password through them).
    [Fact]
    public async Task Contact_change_needs_the_current_password()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var email = $"new-{NewCode()}@mail.com";

        var missing = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email });
        var wrong = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = "Wrong1234" });
        var right = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = Password });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("MSG-GEN-01", await missing.ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-14", await wrong.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Created, right.StatusCode);
    }

    [Fact]
    public async Task Previous_verified_email_is_told_when_the_email_changes()
    {
        var oldEmail = $"old-{NewCode()}@mail.com";
        var owner = await factory.AddAccountAsync(NewCode(), Password, configure: a =>
        {
            a.Email = oldEmail;
            a.EmailVerifiedAt = DateTimeOffset.UtcNow;
        });
        var client = await factory.LoginAsync(owner.Username);
        var email = $"new-{NewCode()}@mail.com";

        var started = await (await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = Password }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var token = await VerifyAsync(client, started.GetProperty("verification").GetProperty("verificationId").GetGuid(), factory.LastEmailCode(email));
        var confirmed = await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token });

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var notice = factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List(oldEmail)[0];
        Assert.Contains("n***@mail.com", notice.Text);
    }

    // ACS-02 phone verification is for an unverified phone; a verified one changes through contact-changes.
    [Fact]
    public async Task Verified_phone_cannot_be_replaced_through_phone_verification()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);

        var response = await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = NewPhone() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
    }

    // Logins write the account row; an open profile form must not turn stale because of them.
    [Fact]
    public async Task Logins_do_not_make_the_profile_or_staff_form_stale()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        // Usernames need a letter; a random hex code can be all digits.
        var staff = await factory.AddAccountAsync($"s{NewCode()}", Password, SubjectType.Staff);
        var zone = await factory.AddZoneAsync(NewCode());
        await factory.GrantAsync(staff, StaffRole.ZoneManager, zone);
        var client = await factory.LoginAsync(owner.Username);
        var profileVersion = (await client.GetFromJsonAsync<JsonElement>("/api/me/profile")).GetProperty("rowVersion").GetString();
        var staffView = await client.GetFromJsonAsync<JsonElement>($"/api/accounts/{staff.Id}");

        await factory.LoginAsync(owner.Username);
        await factory.LoginAsync(staff.Username);
        await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = "Wrong1234" });

        var profile = await client.PutAsJsonAsync("/api/me/profile", new { displayName = "Owner renamed", rowVersion = profileVersion });
        var staffEdit = await client.PutAsJsonAsync($"/api/accounts/{staff.Id}", new
        {
            displayName = "Staff renamed",
            username = staffView.GetProperty("username").GetString(),
            phoneNumber = staffView.GetProperty("phoneNumber").GetString(),
            rowVersion = staffView.GetProperty("rowVersion").GetString(),
        });
        var stale = await client.PutAsJsonAsync("/api/me/profile", new { displayName = "Again", rowVersion = profileVersion });

        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staffEdit.StatusCode);
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
    }

    // One number cannot be flooded through several purposes: sends to a target are capped over all of them, even when this
    // purpose still has codes left under BR-ACCOUNT-09.
    [Fact]
    public async Task Sends_to_one_number_are_capped_across_purposes()
    {
        var victim = NewPhone();
        var sentAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await factory.UseDbAsync(db => db.VerificationCodes.Add(new VerificationCode
        {
            Purpose = VerificationPurpose.AccountRecovery,
            Channel = VerificationChannel.Sms,
            Target = victim,
            CodeHash = NewCode(),
            CreatedAt = sentAt,
            LastSentAt = sentAt,
            ExpiresAt = sentAt,
            SendCount = Application.Features.Verification.VerificationThrottle.MaxSendsPerTargetPerWindow,
            SendTimes = Enumerable.Repeat(sentAt, Application.Features.Verification.VerificationThrottle.MaxSendsPerTargetPerWindow).ToList(),
        }));
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var client = await factory.LoginAsync(trader.Username);

        var refused = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = victim, currentPassword = Password });

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(victim));
    }

    [Fact]
    public async Task Daily_sms_ceiling_answers_msg_account_13()
    {
        await using var api = new AuthApiFactory();
        using var limited = api.WithWebHostBuilder(b => b.UseSetting("Verification:DailySmsLimit", "1"));
        var first = await api.AddAccountAsync(NewCode(), Password, configure: a => a.PhoneVerifiedAt = null);
        var second = await api.AddAccountAsync(NewCode(), Password, configure: a => a.PhoneVerifiedAt = null);

        var sent = await (await LoginOnAsync(limited, first.Username)).PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = first.PhoneNumber });
        var refused = await (await LoginOnAsync(limited, second.Username)).PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = second.PhoneNumber });

        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("MSG-ACCOUNT-13", await refused.ErrorCodeAsync());
    }

    // Every endpoint that sends a code is limited per client, also when logged in.
    [Fact]
    public async Task Authenticated_code_endpoints_are_rate_limited_per_client()
    {
        await using var api = new AuthApiFactory();
        using var limited = api.WithWebHostBuilder(b => b.UseSetting("RateLimits:CodeRequestsPerMinute", "2"));
        var requests = new List<(HttpClient Client, string? Phone)>();
        for (var i = 0; i < 3; i++)
        {
            var account = await api.AddAccountAsync(NewCode(), Password, configure: a => a.PhoneVerifiedAt = null);
            requests.Add((await LoginOnAsync(limited, account.Username), account.PhoneNumber));
        }

        // Together: one after another they can take longer than the window on a busy machine.
        var answers = await Task.WhenAll(requests.Select(r => r.Client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = r.Phone })));

        Assert.Equal(2, answers.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Push_token_taken_over_by_another_account_is_audited()
    {
        var first = await factory.AddAccountAsync(NewCode(), Password);
        var second = await factory.AddAccountAsync(NewCode(), Password);
        var token = $"ExponentPushToken[{NewCode()}]";

        await (await factory.LoginAsync(first.Username)).PostAsJsonAsync("/api/me/devices", new { token, platform = "android", permissionGranted = true });
        await (await factory.LoginAsync(second.Username)).PostAsJsonAsync("/api/me/devices", new { token, platform = "android", permissionGranted = true });

        var moves = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "PushDevice.Moved").ToListAsync());
        Assert.Contains(moves, e => e.Before!.Contains(first.Id.ToString()) && e.After!.Contains(second.Id.ToString()));
    }

    private static async Task<HttpClient> LoginOnAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
