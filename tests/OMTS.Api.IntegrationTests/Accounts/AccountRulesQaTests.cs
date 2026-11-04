using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Verification;
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

    // BR-ACCOUNT-05, BR-ACCOUNT-07: all formats of one number share one count, real or unknown, so the 5th failure answers alike.
    [Fact]
    public async Task Login_failures_share_one_count_over_the_formats_of_a_real_or_an_unknown_number()
    {
        var real = await factory.AddAccountAsync(NewCode(), Pw);

        var forReal = await FailLoginInEveryFormatAsync(real.PhoneNumber!);
        var forUnknown = await FailLoginInEveryFormatAsync(NewPhone());

        Assert.Equal(HttpStatusCode.Locked, forReal[^1]);
        Assert.Equal(forUnknown, forReal);
    }

    // BR-ACCOUNT-05, BR-ACCOUNT-07: the count of an unknown identifier behaves like that of an account. Failures during the pause
    // do not extend it, so polled every 14 minutes it is free again after 15; and the count does not age, so 4 failures, 16 minutes
    // and one more failure pause it.
    [Fact]
    public void An_unknown_identifier_pauses_and_counts_like_an_account()
    {
        var polledClock = new MovableClock();
        using var polled = new UnknownLoginCounter(timeProvider: polledClock);
        for (var i = 0; i < Account.MaxFailedLogins; i++)
        {
            polled.RegisterFailure("polled");
        }

        polledClock.Advance(TimeSpan.FromMinutes(14));
        polled.RegisterFailure("polled");
        var leftAfterFourteenMinutes = polled.PauseLeft("polled");
        polledClock.Advance(TimeSpan.FromMinutes(14));
        var afterThePause = polled.RegisterFailure("polled");

        var spacedClock = new MovableClock();
        using var spaced = new UnknownLoginCounter(timeProvider: spacedClock);
        for (var i = 0; i < Account.MaxFailedLogins - 1; i++)
        {
            spaced.RegisterFailure("spaced");
        }

        spacedClock.Advance(TimeSpan.FromMinutes(16));
        var fifthInARow = spaced.RegisterFailure("spaced");

        Assert.Equal(TimeSpan.FromMinutes(1), leftAfterFourteenMinutes);
        Assert.Equal(1, afterThePause);
        Assert.Null(polled.PauseLeft("polled"));
        Assert.Equal(Account.MaxFailedLogins, fifthInARow);
        Assert.Equal(Account.TemporaryLockDuration, spaced.PauseLeft("spaced"));
    }

    // PM decision 03/11: MSG-ACCOUNT-06 carries the time left of the pause, in retryAfterSeconds and in the text.
    [Fact]
    public async Task Msg_account_06_carries_the_time_left_of_the_pause()
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
        var body = await paused.BodyAsync();
        Assert.InRange(body.GetProperty("retryAfterSeconds").GetInt32(), 1, 60);
        Assert.EndsWith("after 1 minute.", body.GetProperty("detail").GetString());
    }

    // PM decision 03/11: the same on a wrong current check of the signed-in user.
    [Fact]
    public async Task Msg_account_06_on_a_wrong_current_entry_carries_the_time_left_of_the_pause()
    {
        var account = await factory.AddAccountAsync(NewCode(), Pw);
        var client = await factory.LoginAsync(account.Username, Pw);
        await factory.UpdateAsync<Account>(account.Id, a => a.LockoutEndAt = DateTimeOffset.UtcNow.AddSeconds(90));
        var newOne = NewPassword();

        var paused = await client.PostAsJsonAsync("/api/me/password", new { currentPassword = NewPassword(), newPassword = newOne, passwordConfirmation = newOne });

        Assert.Equal("MSG-ACCOUNT-06", await paused.ErrorCodeAsync());
        var body = await paused.BodyAsync();
        Assert.InRange(body.GetProperty("retryAfterSeconds").GetInt32(), 1, 90);
        Assert.EndsWith("after 2 minutes.", body.GetProperty("detail").GetString());
    }

    // BR-ACCOUNT-07: an unknown identifier answers the same 423 with the time left: the first answer is the length of the pause.
    [Fact]
    public async Task An_unknown_identifier_answers_msg_account_06_with_the_length_of_the_pause()
    {
        var client = factory.CreateClient();
        var identifier = NewPhone();
        for (var i = 1; i < Account.MaxFailedLogins; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { identifier, password = NewPassword() });
        }

        var paused = await client.PostAsJsonAsync("/api/auth/login", new { identifier, password = NewPassword() });

        Assert.Equal("MSG-ACCOUNT-06", await paused.ErrorCodeAsync());
        Assert.InRange((await paused.BodyAsync()).GetProperty("retryAfterSeconds").GetInt32(), 895, 900);
    }

    // PM decision 03/11: MSG-ACCOUNT-18 carries the real wait, also when the guard per target refuses. All ten sends leave the
    // hour in one minute, so a client can try again then, and the refusal says how many codes the purpose and number have left.
    [Fact]
    public async Task The_guard_per_target_answers_the_time_until_a_send_leaves_the_hour()
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
        var body = await refused.BodyAsync();
        Assert.InRange(body.GetProperty("retryAfterSeconds").GetInt32(), 1, 120);
        Assert.Equal(VerificationCode.MaxCodesPerHour, body.GetProperty("sendsLeft").GetInt32());
    }

    // The guard counts the sends that are in the hour: a request whose last send is recent does not count its older sends.
    [Fact]
    public async Task The_guard_per_target_does_not_count_sends_older_than_the_hour()
    {
        var phone = NewPhone();
        var now = DateTimeOffset.UtcNow;
        await factory.UseDbAsync(db => db.VerificationCodes.Add(new VerificationCode
        {
            Purpose = VerificationPurpose.ChangePhone,
            Channel = VerificationChannel.Sms,
            Target = phone,
            CodeHash = NewCode(),
            CreatedAt = now.AddHours(-2),
            LastSentAt = now.AddMinutes(-5),
            SendCount = VerificationThrottle.MaxSendsPerTargetPerWindow,
            SendTimes = [now.AddMinutes(-5)],
            ExpiresAt = now,
        }));

        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().StartRegistrationAsync("Sms", phone)).StatusCode);
    }

    // BR-ACCOUNT-09: a code can be entered wrongly 5 times, and a resend gives a new code: one typing error on it after 4 wrong
    // entries on the old one leaves its right code working.
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

    // ACCOUNT-06.0.E2: a resend of a code cancelled by its 5th wrong entry answers MSG-ACCOUNT-17, not -16 whose text offers Resend
    // code again. Recovery keeps -16 (BR-ACCOUNT-07): see VerificationAbuseTests.
    [Fact]
    public async Task Resend_of_a_cancelled_code_answers_msg_account_17()
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

    // BR-ACCOUNT-09, ACCOUNT-06 step 4: only the latest code works, also when the earlier one is on a request opened more than an
    // hour ago that a resend made current.
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

    // Same rule: a new code for the same purpose and number cancels the earlier one, also when another account asked for it.
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

    // BR-ACCOUNT-09, ACCOUNT-03 step 7: a change of the number cancels a recovery code sent to the old one, say because the phone
    // was stolen. It gives no token, and answers as any cancelled code does, without saying why.
    [Fact]
    public async Task A_change_of_the_number_cancels_the_recovery_code_sent_to_the_old_number()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var staff = await AddStaffAsync();
        var thief = factory.CreateClient();
        var requestId = await StartRecoveryAsync(thief, staff.PhoneNumber!);
        var code = factory.LastSmsCode(staff.PhoneNumber!);

        await EditStaffAsync(owner, staff, NewPhone());

        Assert.Equal("MSG-ACCOUNT-16", await (await thief.EnterAsync(requestId, code)).ErrorCodeAsync());
        Assert.Equal(staff.PasswordHash, (await StoredAsync(staff)).PasswordHash);
    }

    // BR-ACCOUNT-09: a token issued on the old number resets nothing after the change either.
    [Fact]
    public async Task A_change_of_the_number_cancels_a_recovery_token_of_the_old_number()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var staff = await AddStaffAsync();
        var thief = factory.CreateClient();
        var token = await VerifyAsync(thief, await StartRecoveryAsync(thief, staff.PhoneNumber!), factory.LastSmsCode(staff.PhoneNumber!));

        await EditStaffAsync(owner, staff, NewPhone());

        Assert.Equal("MSG-ACCOUNT-16", await (await ResetAsync(thief, token)).ErrorCodeAsync());
        Assert.Equal(staff.PasswordHash, (await StoredAsync(staff)).PasswordHash);
    }

    // BR-ACCOUNT-09: the same when the holder changes the number in ACCOUNT-05, and a token of the new number then still works once.
    [Fact]
    public async Task A_change_of_the_number_by_its_holder_cancels_the_recovery_of_the_old_number()
    {
        var holder = await factory.AddAccountAsync(NewCode(), Pw, SubjectType.Trader);
        var oldPhone = holder.PhoneNumber!;
        var client = await factory.LoginAsync(holder.Username, Pw);
        var thief = factory.CreateClient();
        var token = await VerifyAsync(thief, await StartRecoveryAsync(thief, oldPhone), factory.LastSmsCode(oldPhone));

        var newPhone = NewPhone();
        var changeId = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = newPhone, currentPassword = Pw }));
        var changeToken = await VerifyAsync(client, changeId, factory.LastSmsCode(newPhone));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = changeToken })).StatusCode);

        Assert.Equal("MSG-ACCOUNT-16", await (await ResetAsync(thief, token)).ErrorCodeAsync());
        Assert.Equal(newPhone, (await StoredAsync(holder)).PhoneNumber);
    }

    // BR-ACCOUNT-09: removing the email cancels a recovery token of that email; the phone number keeps working.
    [Fact]
    public async Task Removing_the_email_cancels_the_recovery_of_that_email_and_not_of_the_phone()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var email = $"staff-{NewCode()}@example.com";
        var staff = await AddStaffAsync(a => { a.Email = email; a.EmailVerifiedAt = DateTimeOffset.UtcNow; });
        var anonymous = factory.CreateClient();
        var emailRecovery = await StartRecoveryAsync(anonymous, email);
        var emailToken = await VerifyAsync(anonymous, emailRecovery, factory.LastEmailCode(email));
        await factory.AgeAsync(emailRecovery, TwoMinutes);
        var phoneToken = await VerifyAsync(anonymous, await StartRecoveryAsync(anonymous, staff.PhoneNumber!), factory.LastSmsCode(staff.PhoneNumber!));

        await EditStaffAsync(owner, staff, staff.PhoneNumber!);

        Assert.Null((await StoredAsync(staff)).Email);
        Assert.Equal("MSG-ACCOUNT-16", await (await ResetAsync(anonymous, emailToken)).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(anonymous, phoneToken)).StatusCode);
    }

    // ACCOUNT-03 step 7: a token resets only the account that still has that verified contact, also when no change cancelled it.
    [Fact]
    public async Task A_recovery_token_resets_only_the_account_that_still_has_its_contact()
    {
        var staff = await AddStaffAsync();
        var anonymous = factory.CreateClient();
        var token = await VerifyAsync(anonymous, await StartRecoveryAsync(anonymous, staff.PhoneNumber!), factory.LastSmsCode(staff.PhoneNumber!));

        await factory.UpdateAsync<Account>(staff.Id, a => a.PhoneNumber = NewPhone());

        Assert.Equal("MSG-ACCOUNT-16", await (await ResetAsync(anonymous, token)).ErrorCodeAsync());
        Assert.Equal(staff.PasswordHash, (await StoredAsync(staff)).PasswordHash);
    }

    // BR-ACCOUNT-09: a phone step on the old number does not put it back in place of the number the Owner has just entered.
    [Fact]
    public async Task A_change_of_the_number_cancels_the_phone_step_on_the_old_number()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var oldPhone = NewPhone();
        var staff = await AddStaffAsync(a => { a.PhoneNumber = oldPhone; a.PhoneVerifiedAt = null; });
        var client = await factory.LoginAsync(staff.Username, Pw);
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { }));
        var code = factory.LastSmsCode(oldPhone);

        var newPhone = NewPhone();
        await EditStaffAsync(owner, staff, newPhone);

        Assert.Equal("MSG-ACCOUNT-16", await (await client.EnterAsync(id, code)).ErrorCodeAsync());
        Assert.Equal(newPhone, (await StoredAsync(staff)).PhoneNumber);
    }

    // BR-ACCOUNT-09: the same for a token, and for another number the staff member entered before the Owner's change.
    [Fact]
    public async Task A_change_of_the_number_cancels_a_phone_step_token_on_another_number()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var staff = await AddStaffAsync(a => a.PhoneVerifiedAt = null);
        var client = await factory.LoginAsync(staff.Username, Pw);
        var entered = NewPhone();
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = entered }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(entered));

        var newPhone = NewPhone();
        await EditStaffAsync(owner, staff, newPhone);

        Assert.Equal("MSG-ACCOUNT-16", await (await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token })).ErrorCodeAsync());
        Assert.Equal(newPhone, (await StoredAsync(staff)).PhoneNumber);
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

    private async Task<Account> AddStaffAsync(Action<Account>? configure = null)
    {
        var staff = await factory.AddAccountAsync(NewCode(), Pw, SubjectType.Staff, configure);
        await factory.GrantAsync(staff, StaffRole.Accountant, null);
        return staff;
    }

    private static async Task EditStaffAsync(HttpClient owner, Account staff, string phoneNumber)
    {
        var rowVersion = (await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/accounts/{staff.Id}")).GetProperty("rowVersion").GetString();
        var edited = await owner.PutAsJsonAsync($"/api/accounts/{staff.Id}", new { displayName = "A", username = staff.Username, phoneNumber, rowVersion });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    }

    private static async Task<Guid> StartRecoveryAsync(HttpClient client, string identifier) =>
        (await (await client.PostAsJsonAsync("/api/auth/recovery", new { identifier })).BodyAsync()).GetProperty("requestId").GetGuid();

    private static async Task<HttpResponseMessage> ResetAsync(HttpClient client, string token)
    {
        var newPassword = NewPassword();
        return await client.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = token, newPassword, passwordConfirmation = newPassword });
    }

    private Task<Account> StoredAsync(Account account) =>
        factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id));

    private sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
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
