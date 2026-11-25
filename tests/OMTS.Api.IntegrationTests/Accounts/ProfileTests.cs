using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>ACCOUNT-05 personal profile.</summary>
public class ProfileTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Display_name_is_saved_with_the_read_row_version_and_username_is_read_only()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var profile = await client.GetFromJsonAsync<JsonElement>("/api/me/profile");
        var rowVersion = profile.GetProperty("rowVersion").GetString();

        var username = await client.PutAsJsonAsync("/api/me/profile", new { displayName = "New name", rowVersion, username = "hacker" });
        var empty = await client.PutAsJsonAsync("/api/me/profile", new { displayName = " ", rowVersion });
        var stale = await client.PutAsJsonAsync("/api/me/profile", new { displayName = "New name", rowVersion = StaleRowVersion });
        var saved = await client.PutAsJsonAsync("/api/me/profile", new { displayName = "Chủ nhiệm Nguyễn", rowVersion });

        Assert.Equal("MSG-GEN-01", await username.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await empty.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal("Personal profile saved.", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal("Chủ nhiệm Nguyễn", (await client.GetFromJsonAsync<JsonElement>("/api/me/profile")).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task New_email_is_used_only_after_its_code_is_verified()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);
        var email = $"new-{NewCode()}@mail.com";

        var started = await (await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Email", target = email, currentPassword = Password })).Content.ReadFromJsonAsync<JsonElement>();
        var message = started.GetProperty("message").GetString();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/me/profile");
        var token = await VerifyAsync(client, started.GetProperty("verification").GetProperty("verificationId").GetGuid(), factory.LastEmailCode(email));
        var confirmed = await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token });
        var after = await client.GetFromJsonAsync<JsonElement>("/api/me/profile");

        Assert.StartsWith("A verification code has been sent to n***@mail.com", message);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("email").ValueKind);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(email, after.GetProperty("email").GetString());
        Assert.True(after.GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task Contact_of_another_account_or_the_same_verified_value_is_refused_without_a_code()
    {
        var other = await factory.AddAccountAsync(NewCode(), Password);
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var client = await factory.LoginAsync(owner.Username);

        var taken = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = other.PhoneNumber });
        var same = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = owner.PhoneNumber });

        Assert.Equal("MSG-GEN-01", await taken.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await same.ErrorCodeAsync());
    }

    // BR-ACCOUNT-04: the link stays; the offline profile with the new number is neither linked nor merged.
    [Fact]
    public async Task Trader_phone_change_updates_the_own_profile_only()
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var own = new Customer { Code = OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit.NewCustomerCode(), CooperativeId = cooperative.Id, AccountId = trader.Id, Name = "Own", PhoneNumber = trader.PhoneNumber };
        var phone = NewPhone();
        var offline = new Customer { Code = OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit.NewCustomerCode(), CooperativeId = cooperative.Id, Name = "Offline", PhoneNumber = phone };
        await factory.UseDbAsync(db => db.Customers.AddRange(own, offline));
        var client = await factory.LoginAsync(trader.Username);

        var started = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = phone, currentPassword = Password });
        var token = await VerifyAsync(client, await VerificationIdAsync(started), factory.LastSmsCode(phone));
        await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token });

        var stored = await factory.QueryAsync(db => db.Customers.AsNoTracking().Where(c => c.Id == offline.Id || c.Id == own.Id).ToListAsync());
        Assert.Null(stored.Single(c => c.Id == offline.Id).MergedIntoCustomerId);
        Assert.Null(stored.Single(c => c.Id == offline.Id).AccountId);
        Assert.Equal(trader.Id, stored.Single(c => c.Id == own.Id).AccountId);
        Assert.Equal(phone, stored.Single(c => c.Id == own.Id).PhoneNumber);
    }
}
