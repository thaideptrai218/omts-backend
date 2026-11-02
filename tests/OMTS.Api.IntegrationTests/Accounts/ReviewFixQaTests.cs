using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// QA checks for the OMTS-198 review fixes: contact changes need the current password (and lock after five
/// wrong entries), verification codes are stored keyed, and a verified phone is not replaced by phone verification.
/// </summary>
public class ReviewFixQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Contact_change_without_password_is_refused_and_sends_nothing()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var target = NewPhone();

        var response = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Empty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(target));
        var stored = await factory.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == owner.Id));
        Assert.Equal(owner.PhoneNumber, stored.PhoneNumber);
    }

    // Four wrong entries answer MSG17, the fifth locks (MSG69); a correct password is refused while locked.
    [Fact]
    public async Task Wrong_current_passwords_lock_contact_change_and_the_right_one_is_refused_while_locked()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var email = $"qa-{NewCode()}@mail.com";
        object Body(string password) => new { channel = "Email", target = email, currentPassword = password };

        var codes = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            codes.Add((await (await client.PostAsJsonAsync("/api/me/contact-changes", Body("Wrong1234"))).ErrorCodeAsync())!);
        }
        var whileLocked = await client.PostAsJsonAsync("/api/me/contact-changes", Body(Password));

        Assert.Equal(new[] { "MSG-ACCOUNT-14", "MSG-ACCOUNT-14", "MSG-ACCOUNT-14", "MSG-ACCOUNT-14", "MSG-ACCOUNT-06" }, codes);
        Assert.Equal("MSG-ACCOUNT-06", await whileLocked.ErrorCodeAsync());
        var stored = await factory.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == owner.Id));
        Assert.Equal(owner.PhoneNumber, stored.PhoneNumber);
        Assert.Null(stored.Email);
        Assert.Empty(factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List(email));
    }

    // The stored hash must be keyed: a plain SHA-256 of "{id}:{code}" would be brute-forceable in a million tries.
    [Fact]
    public async Task Stored_code_hash_is_keyed_and_not_plain_sha256_of_the_code()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password, configure: a => a.PhoneVerifiedAt = null);
        var client = await factory.LoginAsync(owner.Username);
        var phone = NewPhone();

        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = phone }));
        var code = factory.LastSmsCode(phone);
        var stored = await factory.QueryAsync(db => db.VerificationCodes.SingleAsync(c => c.Id == id));

        Assert.Equal(64, stored.CodeHash.Length);
        Assert.Matches("^[0-9A-Fa-f]{64}$", stored.CodeHash);
        Assert.NotEqual(Sha256Hex($"{id:N}:{code}"), stored.CodeHash);
        Assert.NotEqual(Sha256Hex($"{id}:{code}"), stored.CodeHash);
    }

    // Phone verification refuses a verified phone and touches neither the account nor the new number.
    [Fact]
    public async Task Verified_phone_refusal_leaves_account_and_new_number_untouched()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var newPhone = NewPhone();

        var response = await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = newPhone });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Empty(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(newPhone));
        var stored = await factory.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == owner.Id));
        Assert.Equal(owner.PhoneNumber, stored.PhoneNumber);
    }

    private static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
