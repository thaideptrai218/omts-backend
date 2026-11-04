using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Auth;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Accounts.AccountRulesQaTestsKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// Independent QA of the account rules (OMTS-216): telling accounts apart, and the limits of BR-ACCOUNT-09. A test whose
/// comment starts with QA-n fails on purpose: it shows finding n of reports/qa-261104-account-rules.md.
/// </summary>
public class AccountRulesQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly string Pw = NewPassword();

    // QA-M1 (BR-ACCOUNT-07): the answer for a real identifier waits for the SMS gateway, the answer for an unknown one does not.
    [Fact]
    public async Task Recovery_answer_takes_the_same_time_for_a_real_and_an_unknown_identifier()
    {
        var delay = TimeSpan.FromMilliseconds(1500);
        var real = await factory.AddAccountAsync(NewCode(), Pw);
        await using var slow = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ISmsSender>(new SlowSmsSender(delay))));
        var client = slow.CreateClient();
        await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = NewPhone() });

        var unknown = await TimeAsync(() => client.PostAsJsonAsync("/api/auth/recovery", new { identifier = NewPhone() }));
        var existing = await TimeAsync(() => client.PostAsJsonAsync("/api/auth/recovery", new { identifier = real.PhoneNumber }));

        Assert.True(
            Math.Abs((existing - unknown).TotalMilliseconds) < 1000,
            $"real identifier {existing.TotalMilliseconds:F0} ms, unknown identifier {unknown.TotalMilliseconds:F0} ms");
    }

    // QA-M2 (BR-ACCOUNT-05, BR-ACCOUNT-07): failures of one real number in several formats share one count, those of an
    // unknown number are counted per spelling, so the 5th failure tells them apart: 423 against 401.
    [Fact]
    public async Task Login_failures_in_several_formats_answer_the_same_for_a_real_and_an_unknown_number()
    {
        var real = await factory.AddAccountAsync(NewCode(), Pw);

        var forReal = await FailLoginInEveryFormatAsync(real.PhoneNumber!);
        var forUnknown = await FailLoginInEveryFormatAsync(NewPhone());

        Assert.Equal(forUnknown, forReal);
    }

    // QA-M2, second form: the pause of an unknown identifier starts again with each attempt, and the count of a real account never
    // ages. Polled every 14 minutes, an unknown identifier stays paused where a real account is free again after 15; 4 failures,
    // 16 minutes and one more failure pause a real account but not an unknown identifier. The cache clock is set by reflection,
    // as the counter has no clock of its own.
    [Fact]
    public void The_count_of_an_unknown_identifier_ages_like_the_count_of_an_account()
    {
        using var polled = new UnknownLoginCounter();
        var polledClock = SetClock(polled);
        for (var i = 0; i < Account.MaxFailedLogins; i++)
        {
            polled.RegisterFailure("polled");
        }

        polledClock.Advance(TimeSpan.FromMinutes(14));
        polled.RegisterFailure("polled");
        polledClock.Advance(TimeSpan.FromMinutes(14));
        var afterTheRealPause = polled.RegisterFailure("polled");

        using var spaced = new UnknownLoginCounter();
        var spacedClock = SetClock(spaced);
        for (var i = 0; i < Account.MaxFailedLogins - 1; i++)
        {
            spaced.RegisterFailure("spaced");
        }

        spacedClock.Advance(TimeSpan.FromMinutes(16));
        var fifthInARow = spaced.RegisterFailure("spaced");

        Assert.True(afterTheRealPause < Account.MaxFailedLogins, $"28 minutes after the pause began, the count is {afterTheRealPause}: still paused.");
        Assert.True(fifthInARow >= Account.MaxFailedLogins, $"The 5th failure in a row counts {fifthInARow}: not paused, as an account would be.");
    }

    // QA-H1 (PM decision 03/11): MSG-ACCOUNT-06 carries the real wait. One minute before the pause ends it says 900 seconds.
    [Fact]
    public async Task Login_pause_answers_the_real_wait()
    {
        var account = await factory.AddAccountAsync(NewCode(), Pw);
        var client = factory.CreateClient();
        for (var i = 0; i < Account.MaxFailedLogins; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { identifier = account.Username, password = NewPassword() });
        }

        await factory.UpdateAsync<Account>(account.Id, a => a.LockoutEndAt = DateTimeOffset.UtcNow.AddSeconds(60));
        var paused = await client.PostAsJsonAsync("/api/auth/login", new { identifier = account.Username, password = Pw });

        Assert.Equal("MSG-ACCOUNT-06", await paused.ErrorCodeAsync());
        Assert.InRange((await paused.BodyAsync()).GetProperty("retryAfterSeconds").GetInt32(), 1, 60);
    }

    // QA-L1 (PM decision 03/11): MSG-ACCOUNT-18 carries the real wait, also when the guard per target refuses. All ten
    // sends leave the hour in one minute, and it says 3600 seconds.
    [Fact]
    public async Task Too_many_codes_to_one_target_answers_the_real_wait()
    {
        var phone = NewPhone();
        var sent = DateTimeOffset.UtcNow.AddMinutes(-59);
        await factory.UseDbAsync(db =>
        {
            for (var i = 0; i < 10; i++)
            {
                db.VerificationCodes.Add(new VerificationCode
                {
                    Purpose = VerificationPurpose.ChangePhone,
                    Channel = VerificationChannel.Sms,
                    Target = phone,
                    CodeHash = NewCode(),
                    CreatedAt = sent,
                    LastSentAt = sent,
                    SendTimes = [sent],
                    ExpiresAt = sent + VerificationCode.Lifetime,
                });
            }
        });

        var refused = await factory.CreateClient().StartRegistrationAsync("Sms", phone);

        Assert.Equal("MSG-ACCOUNT-18", await refused.ErrorCodeAsync());
        Assert.InRange((await refused.BodyAsync()).GetProperty("retryAfterSeconds").GetInt32(), 1, 120);
    }

    // QA-M3 (BR-ACCOUNT-09: a code can be entered wrongly 5 times): the new code of a resend inherits the wrong entries of the
    // old one, so after 4 wrong entries the first typing error on the new code cancels it, and its right code is refused.
    [Fact]
    public async Task The_new_code_of_a_resend_has_its_own_five_wrong_entries()
    {
        var phone = NewPhone();
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("MSG-ACCOUNT-16", await (await client.EnterAsync(id, WrongCodeFor(factory.LastSmsCode(phone)))).ErrorCodeAsync());
        }

        await factory.AgeAsync(id, TwoMinutes);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/verifications/{id}/resend", null)).StatusCode);
        var newCode = factory.LastSmsCode(phone);

        Assert.Equal("MSG-ACCOUNT-16", await (await client.EnterAsync(id, WrongCodeFor(newCode))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.EnterAsync(id, newCode)).StatusCode);
    }

    // QA-L2 (ACCOUNT-06.0.E2): resend of a cancelled code answers MSG-ACCOUNT-16, whose text offers Resend code again.
    [Fact]
    public async Task Resend_of_a_cancelled_code_says_it_was_cancelled()
    {
        var phone = NewPhone();
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));
        for (var i = 0; i < VerificationCode.MaxFailedAttempts; i++)
        {
            await client.EnterAsync(id, WrongCodeFor(factory.LastSmsCode(phone)));
        }

        await factory.AgeAsync(id, TwoMinutes);

        Assert.Equal("MSG-ACCOUNT-17", await (await client.PostAsync($"/api/verifications/{id}/resend", null)).ErrorCodeAsync());
    }

    // QA-L3 (BR-ACCOUNT-09, ACCOUNT-06 step 4: only the latest code works): a request opened more than an hour ago is not
    // cancelled by a new code, although a resend made its code current.
    [Fact]
    public async Task A_new_code_cancels_an_earlier_code_of_a_request_older_than_an_hour()
    {
        var phone = NewPhone();
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));
        await factory.AgeAsync(id, TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/verifications/{id}/resend", null)).StatusCode);
        var earlierCode = factory.LastSmsCode(phone);
        await factory.AgeAsync(id, TwoMinutes);

        await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));

        Assert.Equal("MSG-ACCOUNT-16", await (await client.EnterAsync(id, earlierCode)).ErrorCodeAsync());
    }

    // QA-L3 (same rule): the code of one account stays valid after a new code for the same purpose and number to another account.
    [Fact]
    public async Task A_new_code_for_another_account_cancels_the_earlier_code_of_that_number()
    {
        var phone = NewPhone();
        var (first, second) = (await TraderWithoutPhoneAsync(), await TraderWithoutPhoneAsync());
        var id = await VerificationIdAsync(await first.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = phone }));
        var earlierCode = factory.LastSmsCode(phone);
        await factory.AgeAsync(id, TwoMinutes);

        await VerificationIdAsync(await second.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = phone }));

        Assert.Equal("MSG-ACCOUNT-16", await (await first.EnterAsync(id, earlierCode)).ErrorCodeAsync());
    }

    // QA-L4 (BR-ACCOUNT-09 limits one number; ACCOUNT-02.3 lets the user change it): a mistyped number blocks the right one for 60 seconds.
    [Fact]
    public async Task Correcting_a_mistyped_number_does_not_wait_60_seconds()
    {
        var client = await TraderWithoutPhoneAsync();

        await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = NewPhone() }));
        var corrected = await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = NewPhone() });

        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
    }

    // QA-H2 (BR-ACCOUNT-07, BR-ACCOUNT-09, ACCOUNT-03 step 7): a code or token of a recovery survives the change of the number it
    // was sent to. The Owner replaces the number of a staff member, say because the phone was stolen, and the old number still sets the password.
    [Fact]
    public async Task A_recovery_on_a_number_stops_when_the_owner_changes_that_number()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var staff = await factory.AddAccountAsync(NewCode(), Pw, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.Accountant, null);
        var thief = factory.CreateClient();
        var started = await thief.PostAsJsonAsync("/api/auth/recovery", new { identifier = staff.PhoneNumber });
        var requestId = (await started.BodyAsync()).GetProperty("requestId").GetGuid();
        var code = factory.LastSmsCode(staff.PhoneNumber!);

        var rowVersion = (await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/accounts/{staff.Id}")).GetProperty("rowVersion").GetString();
        var edited = await owner.PutAsJsonAsync($"/api/accounts/{staff.Id}", new { displayName = "A", username = staff.Username, phoneNumber = NewPhone(), rowVersion });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var entered = await thief.EnterAsync(requestId, code);
        if (entered.IsSuccessStatusCode)
        {
            var thiefPassword = NewPassword();
            var token = (await entered.BodyAsync()).GetProperty("verificationToken").GetString();
            var reset = await thief.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = token, newPassword = thiefPassword, passwordConfirmation = thiefPassword });
            Assert.Equal("MSG-ACCOUNT-16", await reset.ErrorCodeAsync());
        }

        var stored = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == staff.Id));
        Assert.Equal(staff.PasswordHash, stored.PasswordHash);
    }

    // QA-L5 (BR-ACCOUNT-09: a change of the number cancels the earlier code): the token for the old number puts it back
    // verified, in place of the number the Owner has just entered.
    [Fact]
    public async Task A_phone_step_on_a_number_stops_when_the_owner_changes_that_number()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var oldPhone = NewPhone();
        var staff = await factory.AddAccountAsync(NewCode(), Pw, SubjectType.Staff, a => { a.PhoneNumber = oldPhone; a.PhoneVerifiedAt = null; });
        await factory.GrantAsync(staff, StaffRole.Accountant, null);
        var client = await factory.LoginAsync(staff.Username, Pw);
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { }));
        var code = factory.LastSmsCode(oldPhone);

        var rowVersion = (await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/accounts/{staff.Id}")).GetProperty("rowVersion").GetString();
        var newPhone = NewPhone();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/accounts/{staff.Id}", new { displayName = "A", username = staff.Username, phoneNumber = newPhone, rowVersion })).StatusCode);

        var entered = await client.EnterAsync(id, code);
        if (entered.IsSuccessStatusCode)
        {
            var token = (await entered.BodyAsync()).GetProperty("verificationToken").GetString();
            await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token });
        }

        Assert.Equal(newPhone, (await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == staff.Id))).PhoneNumber);
    }

    // QA-M4 (BR-ACCOUNT-02, BR-ACCOUNT-09): \d takes any Unicode digit, so one number in other digits is a new "number".
    [Theory]
    [InlineData("0٩١٢٣٤٥٦٧٨")]
    [InlineData("0９１２３４５６７８")]
    [InlineData("0९१२३४५६७८")]
    public void A_number_in_other_digits_is_refused_or_normalised(string input)
    {
        var normalised = PhoneNumbers.Normalize(input);

        Assert.True(normalised is null || normalised == "0912345678", $"Normalised to a different number: {normalised}");
    }

    // QA-M4 (BR-ACCOUNT-02, BR-ACCOUNT-09): a quoted local part is the same mailbox, but a different "email" here.
    [Theory]
    [InlineData("\"qa\"@example.com")]
    [InlineData("\"q\\a\"@example.com")]
    [InlineData("qa@example.com.")]
    public void A_quoted_email_is_refused_or_normalised(string input)
    {
        var normalised = EmailAddresses.Normalize(input);

        Assert.True(normalised is null || normalised == "qa@example.com", $"Normalised to a different email: {normalised}");
    }

    // QA-M4 (BR-ACCOUNT-02, BR-ACCOUNT-09): the mailbox of an account, quoted: the check passes, a code goes out, and the account can follow.
    [Fact]
    public async Task A_taken_email_in_quotes_answers_msg_account_03()
    {
        var local = NewCode();
        await factory.AddAccountAsync(NewCode(), Pw, SubjectType.Trader, a => { a.Email = $"{local}@example.com"; a.EmailVerifiedAt = DateTimeOffset.UtcNow; });

        var started = await factory.CreateClient().StartRegistrationAsync("Email", $"\"{local}\"@example.com");

        Assert.Equal("MSG-ACCOUNT-03", await started.ErrorCodeAsync());
    }

    // Fine: case and format of the contact do not get around the limit, and verifying a code does not clear it.
    [Fact]
    public async Task The_limit_holds_across_case_format_and_successful_verifications()
    {
        var email = $"limit-{NewCode()}@example.com";
        var phone = NewPhone();
        var client = factory.CreateClient();

        foreach (var (channel, spellings) in new[]
        {
            ("Email", new[] { email, email.ToUpperInvariant(), $"  {email}  ", email }),
            ("Sms", new[] { phone, "+84" + phone[1..], $"{phone[..4]} {phone[4..7]}.{phone[7..]}", "84" + phone[1..] }),
        })
        {
            for (var i = 0; i < VerificationCode.MaxCodesPerHour; i++)
            {
                var id = await VerificationIdAsync(await client.StartRegistrationAsync(channel, spellings[i]));
                Assert.NotEmpty(await VerifyAsync(client, id, channel == "Sms" ? factory.LastSmsCode(phone) : factory.LastEmailCode(email)));
                await factory.AgeAsync(id, TwoMinutes);
            }

            var refused = await client.StartRegistrationAsync(channel, spellings[3]);
            Assert.Equal("MSG-ACCOUNT-18", await refused.ErrorCodeAsync());
            Assert.InRange((await refused.BodyAsync()).GetProperty("retryAfterSeconds").GetInt32(), 3000, 3600);
        }

        Assert.Equal(3, factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone).Count);
        Assert.Equal(3, factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List(email).Count);
    }

    // Fine: a cancelled code is refused even when right, and an expired one too.
    [Fact]
    public async Task A_cancelled_or_expired_code_is_refused_when_right()
    {
        var phone = NewPhone();
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", phone));
        var code = factory.LastSmsCode(phone);
        var answers = new List<string?>();
        for (var i = 0; i < VerificationCode.MaxFailedAttempts; i++)
        {
            answers.Add(await (await client.EnterAsync(id, WrongCodeFor(code))).ErrorCodeAsync());
        }

        Assert.Equal(["MSG-ACCOUNT-16", "MSG-ACCOUNT-16", "MSG-ACCOUNT-16", "MSG-ACCOUNT-16", "MSG-ACCOUNT-17"], answers);
        Assert.Equal("MSG-ACCOUNT-17", await (await client.EnterAsync(id, code)).ErrorCodeAsync());

        var otherPhone = NewPhone();
        var expiring = await VerificationIdAsync(await client.StartRegistrationAsync("Sms", otherPhone));
        await factory.UpdateAsync<VerificationCode>(expiring, c => c.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Equal("MSG-ACCOUNT-16", await (await client.EnterAsync(expiring, factory.LastSmsCode(otherPhone))).ErrorCodeAsync());
    }

    // Fine: at the limit, the recovery of a real number answers exactly like an unknown number.
    [Fact]
    public async Task At_the_limit_a_real_recovery_answers_like_an_unknown_one()
    {
        var real = await factory.AddAccountAsync(NewCode(), Pw);
        var client = factory.CreateClient();
        for (var i = 0; i < VerificationCode.MaxCodesPerHour; i++)
        {
            await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = real.PhoneNumber });
            foreach (var id in await factory.QueryAsync(db => db.VerificationCodes.Where(c => c.AccountId == real.Id).Select(c => c.Id).ToListAsync()))
            {
                await factory.AgeAsync(id, TwoMinutes);
            }
        }

        var atLimit = await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = real.PhoneNumber });
        var unknown = await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = NewPhone() });

        Assert.Equal(unknown.StatusCode, atLimit.StatusCode);
        Assert.Equal(
            (await unknown.BodyAsync()).EnumerateObject().Select(p => p.Name),
            (await atLimit.BodyAsync()).EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(real.PhoneNumber!).Count);
    }

    private static MovableClock SetClock(UnknownLoginCounter counter)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var cache = typeof(UnknownLoginCounter).GetField("_cache", Private)!.GetValue(counter)!;
        var options = (MemoryCacheOptions)typeof(MemoryCache).GetField("_options", Private)!.GetValue(cache)!;
        var clock = new MovableClock();
        options.Clock = clock;
        return clock;
    }

    private sealed class MovableClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private async Task<HttpClient> TraderWithoutPhoneAsync()
    {
        var trader = await factory.AddAccountAsync(NewCode(), Pw, SubjectType.Trader, a =>
        {
            a.PhoneNumber = null;
            a.PhoneVerifiedAt = null;
            a.Email = $"trader-{NewCode()}@example.com";
            a.EmailVerifiedAt = DateTimeOffset.UtcNow;
        });
        return await factory.LoginAsync(trader.Username, Pw);
    }

    private async Task<List<HttpStatusCode>> FailLoginInEveryFormatAsync(string phone)
    {
        var client = factory.CreateClient();
        var answers = new List<HttpStatusCode>();
        foreach (var spelling in new[] { phone, "+84" + phone[1..], "84" + phone[1..], $"{phone[..4]} {phone[4..]}", $"{phone[..4]}.{phone[4..]}" })
        {
            answers.Add((await client.PostAsJsonAsync("/api/auth/login", new { identifier = spelling, password = NewPassword() })).StatusCode);
        }

        return answers;
    }

    private static async Task<TimeSpan> TimeAsync(Func<Task<HttpResponseMessage>> call)
    {
        var watch = Stopwatch.StartNew();
        var response = await call();
        watch.Stop();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return watch.Elapsed;
    }
}
