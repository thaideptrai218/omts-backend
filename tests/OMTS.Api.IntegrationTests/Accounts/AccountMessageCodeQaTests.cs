using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// QA of OMTS-216 PR 0.2 on ACCOUNT-01 to ACCOUNT-07 (SRS 27/10 Part II §7, Part V §2): each error has the code the
/// use case step cites, the filled-in SRS text and its legacy code; each success text carries its code.
/// </summary>
public class AccountMessageCodeQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string TooManyCodes = @"^You have asked for too many codes\. Please try again after (\d+ seconds|1 minute)\.$";

    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string ChangedPassword = NewPassword();
    private static readonly string WrongPassword = NewPassword();

    // ACCOUNT-05.1.E2: MSG-ACCOUNT-09 naming the field (MSG01 before); ACCOUNT-05.1.E1 stays MSG-GEN-01.
    [Fact]
    public async Task Contact_change_to_a_contact_of_another_account_is_msg_account_09_naming_the_field()
    {
        var emailOfOther = $"taken-{NewCode()}@example.com";
        var other = await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.Email = emailOfOther; a.EmailVerifiedAt = DateTimeOffset.UtcNow; });
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);

        var phone = await (await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = other.PhoneNumber, currentPassword = Password }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-09", "MSG01");
        var email = await (await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = emailOfOther.ToUpperInvariant(), currentPassword = Password }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-09", "MSG01");
        var same = await (await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = owner.PhoneNumber, currentPassword = Password }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");

        Assert.Equal("This phone number is already used by another account. Please enter another one.", phone.Detail());
        Assert.Equal("Phone number", phone.Str("field"));
        Assert.Equal("This email is already used by another account. Please enter another one.", email.Detail());
        Assert.Equal("Email", email.Str("field"));
        Assert.Equal("Phone number is missing or invalid. Please check and enter it again.", same.Detail());
    }

    // ACCOUNT-06 step 2: MSG-ACCOUNT-19 with the masked address and the 5-minute lifetime (BR-ACCOUNT-09);
    // the confirmed change answers MSG-GEN-05, and a contact taken meanwhile MSG-ACCOUNT-09.
    [Fact]
    public async Task Contact_change_carries_msg_account_19_then_msg_gen_05_and_a_contact_taken_meanwhile_is_refused()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var email = $"new-{NewCode()}@mail.com";
        var phone = NewPhone();

        var started = await (await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = Password }))
            .BodyAsync(HttpStatusCode.Created);
        var token = await VerifyAsync(client, started.GetProperty("verification").GetProperty("verificationId").GetGuid(), factory.LastEmailCode(email));
        var confirmed = await (await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token })).BodyAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-ACCOUNT-19", started.Str("messageCode"));
        Assert.Equal("A verification code has been sent to n***@mail.com. It expires in 5 minutes.", started.Str("message"));
        Assert.Equal("MSG-GEN-05", confirmed.Str("noticeCode"));
        Assert.Equal("Personal profile saved.", confirmed.Str("notice"));

        // The phone number is taken by another account between the code and the confirmation.
        var phoneStart = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = phone, currentPassword = Password });
        var phoneToken = await VerifyAsync(client, await VerificationIdAsync(phoneStart), factory.LastSmsCode(phone));
        await factory.AddAccountAsync(NewCode(), Password, configure: a => a.PhoneNumber = phone);
        var refused = await (await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = phoneToken }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-09", "MSG01");
        Assert.Equal("Phone number", refused.Str("field"));
    }

    // ACCOUNT-05 step 4 (MSG-GEN-05) and the read-only username: MSG-GEN-01 "Username" (MSG20 before).
    [Fact]
    public async Task Profile_save_carries_msg_gen_05_and_a_username_is_msg_gen_01_with_legacy_msg20()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var rowVersion = (await client.GetFromJsonAsync<JsonElement>("/api/me/profile")).GetProperty("rowVersion").GetString();

        var username = await (await client.PutAsJsonAsync("/api/me/profile", new { displayName = "Name", rowVersion, username = "other" }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG20");
        var saved = await (await client.PutAsJsonAsync("/api/me/profile", new { displayName = "Chủ nhiệm", rowVersion })).BodyAsync(HttpStatusCode.OK);

        Assert.Equal("Username is missing or invalid. Please check and enter it again.", username.Detail());
        Assert.Equal("Username", username.Str("field"));
        Assert.Equal(("Personal profile saved.", "MSG-GEN-05"), (saved.Str("message"), saved.Str("messageCode")));
    }

    // ACCOUNT-07 step 3: MSG-GEN-05; MSG-ACCOUNT-21 only when push is refused by every device.
    [Fact]
    public async Task Notification_preferences_save_carries_msg_gen_05_and_no_notice_code_when_push_is_not_asked()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);

        var saved = await (await client.PutAsJsonAsync("/api/me/notification-preferences", new { items = new[] { new { category = "WORK", channel = "InApp", enabled = true } } }))
            .BodyAsync(HttpStatusCode.OK);

        Assert.Equal(("Notification settings saved.", "MSG-GEN-05"), (saved.Str("message"), saved.Str("messageCode")));
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("notice").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("noticeCode").ValueKind);
    }

    // ACCOUNT-03: MSG-ACCOUNT-20 for every identifier (BR-ACCOUNT-07), MSG-ACCOUNT-10 after the reset.
    [Fact]
    public async Task Recovery_carries_msg_account_20_then_msg_account_10()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = factory.CreateClient();

        var known = await (await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = owner.PhoneNumber })).BodyAsync(HttpStatusCode.Accepted);
        var unknown = await (await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = NewPhone() })).BodyAsync(HttpStatusCode.Accepted);
        var token = await VerifyAsync(client, known.GetProperty("requestId").GetGuid(), factory.LastSmsCode(owner.PhoneNumber!));
        var reset = await (await client.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = token, newPassword = ChangedPassword, passwordConfirmation = ChangedPassword }))
            .BodyAsync(HttpStatusCode.OK);

        foreach (var answer in new[] { known, unknown })
        {
            Assert.Equal("MSG-ACCOUNT-20", answer.Str("messageCode"));
            Assert.Equal("If this phone number or email belongs to an account, a verification code has been sent to it.", answer.Str("message"));
        }

        Assert.Equal(("Your password has been changed. Please log in with your new password.", "MSG-ACCOUNT-10"), (reset.Str("message"), reset.Str("messageCode")));
    }

    // ACCOUNT-01.1: several offline profiles share the number → MSG-ACCOUNT-02 as the notice (MSG57 before); one → no notice.
    [Theory]
    [InlineData(1, null)]
    [InlineData(2, "MSG-ACCOUNT-02")]
    public async Task Registration_notice_code_follows_the_number_of_offline_profiles(int offlineProfiles, string? noticeCode)
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var phone = NewPhone();
        await factory.UseDbAsync(db => db.Customers.AddRange(Enumerable.Range(0, offlineProfiles)
            .Select(i => new Customer { CooperativeId = cooperative.Id, Name = $"Offline {i}", PhoneNumber = phone })));
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(phone));

        var registered = await (await client.PostAsJsonAsync("/api/auth/register", new
        {
            channel = "Sms", identifier = phone, password = Password, passwordConfirmation = Password, verificationToken = token,
        })).BodyAsync(HttpStatusCode.Created);

        Assert.Equal(noticeCode, registered.Str("noticeCode"));
        Assert.Equal(
            noticeCode is null ? null : "Your account has been created. Invoices from before today will appear in your account after the cooperative adds them. Please log in.",
            registered.Str("notice"));
    }

    // ACCOUNT-01.0.E1: MSG-ACCOUNT-03 naming the phone number or the email (MSG14 before).
    [Fact]
    public async Task Registration_with_a_registered_phone_or_email_is_msg_account_03_naming_it()
    {
        var email = $"trader-{NewCode()}@example.com";
        var existing = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader, a => { a.Email = email; a.EmailVerifiedAt = DateTimeOffset.UtcNow; });
        var client = factory.CreateClient();

        var phone = await (await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = existing.PhoneNumber }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-ACCOUNT-03", "MSG14");
        var mail = await (await client.PostAsJsonAsync("/api/verifications", new { channel = "Email", target = email }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-ACCOUNT-03", "MSG14");

        Assert.Equal("This phone number is already registered. Please log in, or select Forgot password if you have forgotten your password.", phone.Detail());
        Assert.Equal("Phone number", phone.Str("field"));
        Assert.Equal("This email is already registered. Please log in, or select Forgot password if you have forgotten your password.", mail.Detail());
        Assert.Equal("Email", mail.Str("field"));
    }

    // GFR-06: MSG-ACCOUNT-13 naming the channel (MSG04 "The SMS service is not responding" before); nothing is kept.
    [Fact]
    public async Task Code_that_cannot_be_sent_is_msg_account_13_naming_sms_or_email()
    {
        var (phone, email) = (NewPhone(), $"down-{NewCode()}@example.com");
        await using var failing = factory.WithWebHostBuilder(b => b.UseSetting("Sms:Fake:FailFor:0", phone).UseSetting("Email:Fake:FailFor:0", email));
        var client = failing.CreateClient();

        var sms = await (await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }))
            .SrsProblemAsync(HttpStatusCode.ServiceUnavailable, "MSG-ACCOUNT-13", "MSG04");
        var mail = await (await client.PostAsJsonAsync("/api/verifications", new { channel = "Email", target = email }))
            .SrsProblemAsync(HttpStatusCode.ServiceUnavailable, "MSG-ACCOUNT-13", "MSG04");

        Assert.Equal("The verification code could not be sent by SMS. Nothing has been changed. Please try again later.", sms.Detail());
        Assert.Equal("The verification code could not be sent by email. Nothing has been changed. Please try again later.", mail.Detail());
    }

    // ACCOUNT-06.1.E1 / BR-ACCOUNT-09: MSG-ACCOUNT-18 with the retry time (TOO_MANY_REQUESTS before).
    [Fact]
    public async Task Too_early_new_code_is_msg_account_18_with_the_retry_time()
    {
        var phone = NewPhone();
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));

        var restart = await (await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }))
            .SrsProblemAsync(HttpStatusCode.TooManyRequests, "MSG-ACCOUNT-18", "TOO_MANY_REQUESTS");
        var resend = await (await client.PostAsync($"/api/verifications/{id}/resend", null))
            .SrsProblemAsync(HttpStatusCode.TooManyRequests, "MSG-ACCOUNT-18", "TOO_MANY_REQUESTS");

        Assert.Matches(TooManyCodes, restart.Detail());
        Assert.InRange(restart.GetProperty("retryAfterSeconds").GetInt32(), 1, 60);
        Assert.Matches(TooManyCodes, resend.Detail());
        Assert.Equal(VerificationCode.MaxSends - 1, resend.GetProperty("sendsLeft").GetInt32());
    }

    // ACCOUNT-06.0.E1: MSG-ACCOUNT-16 (MSG16 before).
    [Fact]
    public async Task Wrong_code_is_msg_account_16_with_the_srs_text()
    {
        var phone = NewPhone();
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));

        var wrong = await (await client.PostAsJsonAsync($"/api/verifications/{id}/verify", new { code = WrongCodeFor(factory.LastSmsCode(phone)) }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-16", "MSG16");

        Assert.Equal("The verification code is wrong or has expired. Please check it, or select Resend code to get a new one.", wrong.Detail());
    }

    // SRS ACCOUNT-06.0.E2: the wrong entry that cancels the code says so with MSG-ACCOUNT-17, and so does a later entry
    // on the cancelled code (both 400, MSG16 before).
    // Recovery is the exception: its ids must stay indistinguishable from random ones (BR-ACCOUNT-07).
    [Fact]
    public async Task Fifth_wrong_entry_of_a_registration_code_is_msg_account_17()
    {
        var phone = NewPhone();
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));
        var wrong = WrongCodeFor(factory.LastSmsCode(phone));

        for (var i = 1; i < VerificationCode.MaxFailedAttempts; i++)
        {
            await (await client.PostAsJsonAsync($"/api/verifications/{id}/verify", new { code = wrong })).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-16", "MSG16");
        }

        var fifth = await client.PostAsJsonAsync($"/api/verifications/{id}/verify", new { code = wrong });
        var later = await client.PostAsJsonAsync($"/api/verifications/{id}/verify", new { code = wrong });

        Assert.Equal("MSG-ACCOUNT-17", await fifth.ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-17", await later.ErrorCodeAsync());
    }

    // MSG-ACCOUNT-17 tells the user to "start again to get a new code"; a start refused by the hourly budget of wrong
    // codes must not answer it (the start itself is what is refused): MSG-ACCOUNT-18 with the retry time.
    [Fact]
    public async Task Start_refused_after_ten_wrong_codes_gives_a_retry_time_instead_of_start_again()
    {
        var phone = NewPhone();
        var client = factory.CreateClient();
        for (var request = 0; request < 2; request++)
        {
            var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone }));
            var wrong = WrongCodeFor(factory.LastSmsCode(phone));
            for (var i = 0; i < VerificationCode.MaxFailedAttempts; i++)
            {
                await client.PostAsJsonAsync($"/api/verifications/{id}/verify", new { code = wrong });
            }

            await AgeRequestsToAsync(phone);
        }

        var refused = await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone });

        Assert.NotEqual("MSG-ACCOUNT-17", await refused.ErrorCodeAsync());
        // The HTTP status stays 423 (lead decision: statuses unchanged); the old code was MSG69.
        var body = await refused.SrsProblemAsync(HttpStatusCode.Locked, "MSG-ACCOUNT-18", "MSG69");
        Assert.True(body.GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    // ACCOUNT-02.1.E2 (MSG-ACCOUNT-09, MSG01 before) and ACCOUNT-04 texts with their legacy codes.
    [Fact]
    public async Task Phone_step_and_password_change_refusals_carry_their_srs_texts_and_legacy_codes()
    {
        var other = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.AddRoleAsync($"R {NewCode()}", (PermissionFunctions.WaterMeasurements, View: true, Create: false, Edit: false));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.PhoneVerifiedAt = null);
        await factory.GrantAsync(staff, role, zone);
        var staffClient = await factory.LoginAsync(staff.Username);
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);

        var taken = await (await staffClient.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = other.PhoneNumber }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-09", "MSG01");
        var wrongCurrent = await (await owner.PostAsJsonAsync("/api/me/password", new { currentPassword = WrongPassword, newPassword = ChangedPassword, passwordConfirmation = ChangedPassword }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-14", "MSG17");
        var noEmail = await (await owner.PostAsync("/api/me/password/code", null))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-15", "NO_VERIFIED_EMAIL");

        Assert.Equal("This phone number is already used by another account. Please enter another one.", taken.Detail());
        Assert.Equal("Phone number", taken.Str("field"));
        Assert.Equal("The current password is incorrect. Your password has not been changed.", wrongCurrent.Detail());
        Assert.Equal(
            "Your account has no verified email, so no code can be sent. Use Forgot password with your phone number, or add an email in your personal profile.",
            noEmail.Detail());
    }

    // The current-password check counts like a login (deviation 4): the 5th failure is MSG-ACCOUNT-06 (MSG69 before), 423.
    [Fact]
    public async Task Fifth_wrong_current_password_is_msg_account_06()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);

        for (var i = 1; i < Account.MaxFailedLogins; i++)
        {
            await (await client.PostAsJsonAsync("/api/me/password", new { currentPassword = WrongPassword, newPassword = ChangedPassword, passwordConfirmation = ChangedPassword }))
                .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-14", "MSG17");
        }

        var paused = await (await client.PostAsJsonAsync("/api/me/password", new { currentPassword = WrongPassword, newPassword = ChangedPassword, passwordConfirmation = ChangedPassword }))
            .SrsProblemAsync(HttpStatusCode.Locked, "MSG-ACCOUNT-06", "MSG69");
        Assert.Equal("Too many failed login attempts. Please try again after 15 minutes.", paused.Detail());
    }

    private static string WrongCodeFor(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    /// <summary>Moves the registration requests of the target two minutes into the past.</summary>
    private async Task AgeRequestsToAsync(string target)
    {
        var ids = await factory.QueryAsync(db => db.VerificationCodes.Where(c => c.Target == target).Select(c => c.Id).ToListAsync());
        foreach (var id in ids)
        {
            await factory.UpdateAsync<VerificationCode>(id, c =>
            {
                c.CreatedAt -= TimeSpan.FromMinutes(2);
                c.LastSentAt -= TimeSpan.FromMinutes(2);
            });
        }
    }
}
