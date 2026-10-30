using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>ACS-02 phone step, ACS-03 recovery and ACS-04 password change.</summary>
public class PhoneAndPasswordTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public async Task Trader_phone_verification_merges_exactly_one_offline_profile(int offlineProfiles, bool hidden)
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader, a => { a.PhoneNumber = null; a.PhoneVerifiedAt = null; });
        var own = new Customer { CooperativeId = cooperative.Id, AccountId = trader.Id, Name = "Own" };
        var phone = NewPhone();
        var offline = Enumerable.Range(0, offlineProfiles).Select(i => new Customer { CooperativeId = cooperative.Id, Name = $"Offline {i}", PhoneNumber = phone }).ToList();
        await factory.UseDbAsync(db => { db.Customers.Add(own); db.Customers.AddRange(offline); });
        var client = await factory.LoginAsync(trader.Username);

        // Every other function stays closed until the phone is verified (MSG67).
        Assert.Equal("MSG67", await (await client.GetAsync("/api/me/permissions")).ErrorCodeAsync());

        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = phone }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(phone));
        var confirmed = await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token });

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(hidden, (await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("oldDocumentsHidden").GetBoolean());
        var merged = await factory.QueryAsync(db => db.Customers.CountAsync(c => c.MergedIntoCustomerId == own.Id));
        Assert.Equal(offlineProfiles == 1 ? 1 : 0, merged);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me/permissions")).StatusCode);
    }

    [Fact]
    public async Task Staff_confirm_the_number_the_owner_entered_and_cannot_take_another_accounts_number()
    {
        var other = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.AddRoleAsync($"R {NewCode()}", (PermissionFunctions.WaterMeasurements, View: true, Create: false, Edit: false));
        var phone = NewPhone();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => { a.PhoneNumber = phone; a.PhoneVerifiedAt = null; });
        await factory.GrantAsync(staff, role, zone);
        var client = await factory.LoginAsync(staff.Username);

        var taken = await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = other.PhoneNumber });
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(phone));
        var confirmed = await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token });

        Assert.Equal("MSG01", await taken.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.NotNull(await factory.QueryAsync(db => db.Accounts.Where(a => a.Id == staff.Id).Select(a => a.PhoneVerifiedAt).SingleAsync()));
    }

    // ACS-03: the same answer for an existing and an unknown identifier.
    [Fact]
    public async Task Recovery_start_answers_the_same_for_known_and_unknown_identifiers()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = factory.CreateClient();

        var known = await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = owner.PhoneNumber });
        var unknown = await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = NewPhone() });

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        var (k, u) = (await known.Content.ReadFromJsonAsync<JsonElement>(), await unknown.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(k.GetProperty("message").GetString(), u.GetProperty("message").GetString());
        Assert.Equal(k.EnumerateObject().Select(p => p.Name), u.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Recovery_reset_sets_the_password_and_ends_every_session()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.MustChangePassword = true; a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddDays(-2); });
        var client = factory.CreateClient();
        var start = await (await client.PostAsJsonAsync("/api/auth/recovery", new { identifier = owner.PhoneNumber })).Content.ReadFromJsonAsync<JsonElement>();
        var token = await VerifyAsync(client, start.GetProperty("requestId").GetGuid(), factory.LastSmsCode(owner.PhoneNumber!));

        var weak = await client.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = token, newPassword = "weak", passwordConfirmation = "weak" });
        var reset = await client.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = token, newPassword = "NewPass2026", passwordConfirmation = "NewPass2026" });
        var reused = await client.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = token, newPassword = "Other2026x", passwordConfirmation = "Other2026x" });

        Assert.Equal("MSG01", await weak.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal("MSG16", await reused.ErrorCodeAsync());
        var tokens = await LoginTokensAsync(factory.CreateClient(), owner.Username, "NewPass2026");
        Assert.False(tokens.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task Change_password_with_the_current_password_returns_fresh_tokens_and_ends_old_sessions()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var other = await factory.LoginAsync(owner.Username);
        var client = await factory.LoginAsync(owner.Username);

        var wrong = await client.PostAsJsonAsync("/api/me/password", new { currentPassword = "Wrong123", newPassword = "NewPass2026", passwordConfirmation = "NewPass2026" });
        var changed = await client.PostAsJsonAsync("/api/me/password", new { currentPassword = Password, newPassword = "NewPass2026", passwordConfirmation = "NewPass2026" });

        Assert.Equal("MSG17", await wrong.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var body = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Your password has been changed.", body.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        var fresh = factory.CreateClient();
        fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("tokens").GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Change_password_with_an_email_code_or_guidance_without_a_verified_email()
    {
        var email = $"owner-{NewCode()}@example.com";
        var withEmail = await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.Email = email; a.EmailVerifiedAt = DateTimeOffset.UtcNow; });
        var withoutEmail = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(withEmail.Username);

        var noEmail = await (await factory.LoginAsync(withoutEmail.Username)).PostAsync("/api/me/password/code", null);
        var id = await VerificationIdAsync(await client.PostAsync("/api/me/password/code", null));
        var token = await VerifyAsync(client, id, factory.LastEmailCode(email));
        var changed = await client.PostAsJsonAsync("/api/me/password", new { verificationToken = token, newPassword = "NewPass2026", passwordConfirmation = "NewPass2026" });

        Assert.Equal("NO_VERIFIED_EMAIL", await noEmail.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    // ACS-02 2c: with a temporary password only the change is open; afterwards everything is.
    [Fact]
    public async Task Forced_change_after_a_temporary_password_opens_the_other_functions()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.MustChangePassword = true; a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow; });
        var client = await factory.LoginAsync(owner.Username);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", await (await client.GetAsync("/api/me/permissions")).ErrorCodeAsync());

        var changed = await (await client.PostAsJsonAsync("/api/me/password", new { currentPassword = Password, newPassword = "NewPass2026", passwordConfirmation = "NewPass2026" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.GetProperty("tokens").GetProperty("accessToken").GetString());

        Assert.False(changed.GetProperty("tokens").GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me/permissions")).StatusCode);
    }
}
