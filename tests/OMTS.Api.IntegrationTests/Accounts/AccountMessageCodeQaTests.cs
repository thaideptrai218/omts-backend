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

    // ACCOUNT-02.1.E2 (MSG-ACCOUNT-09, MSG01 before) and ACCOUNT-04 texts with their legacy codes. The email code for a
    // password change (MSG-ACCOUNT-15) is gone: the current password is the only proof.
    [Fact]
    public async Task Phone_step_and_password_change_refusals_carry_their_srs_texts_and_legacy_codes()
    {
        var other = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var zone = await factory.AddZoneAsync(NewCode());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.PhoneVerifiedAt = null);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, zone);
        var staffClient = await factory.LoginAsync(staff.Username);
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);

        var taken = await (await staffClient.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = other.PhoneNumber }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-09", "MSG01");
        var wrongCurrent = await (await owner.PostAsJsonAsync("/api/me/password", new { currentPassword = WrongPassword, newPassword = ChangedPassword, passwordConfirmation = ChangedPassword }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-ACCOUNT-14", "MSG17");

        Assert.Equal("This phone number is already used by another account. Please enter another one.", taken.Detail());
        Assert.Equal("Phone number", taken.Str("field"));
        Assert.Equal("The current password is incorrect. Your password has not been changed.", wrongCurrent.Detail());
    }

    // The current-password check counts like a login (deviation 4): the 5th failure is MSG-ACCOUNT-06 (MSG69 before), 423, with the whole pause left.
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
        Assert.Equal(900, paused.GetProperty("retryAfterSeconds").GetInt32());
    }
}
