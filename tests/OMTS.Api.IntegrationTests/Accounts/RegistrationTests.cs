using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>ACS-01 – trader registration with a verified phone number or email.</summary>
public class RegistrationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Phone_registration_links_exactly_one_offline_profile(int offlineProfiles, bool hidden)
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var phone = NewPhone();
        var offline = Enumerable.Range(0, offlineProfiles)
            .Select(i => new Customer { CooperativeId = cooperative.Id, Name = $"Offline {i}", PhoneNumber = phone })
            .ToList();
        await factory.UseDbAsync(db => db.Customers.AddRange(offline));

        var registered = await RegisterAsync("Sms", phone);

        Assert.Equal(hidden, registered.GetProperty("oldDocumentsHidden").GetBoolean());
        var accountId = registered.GetProperty("accountId").GetGuid();
        var linked = await factory.QueryAsync(db => db.Customers.SingleAsync(c => c.AccountId == accountId));
        Assert.Equal(offlineProfiles == 1, linked.Id == offline.FirstOrDefault()?.Id);
        Assert.Equal(phone, linked.PhoneNumber);

        // The new trader logs in with the verified phone number.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = phone, password = Password })).StatusCode);
    }

    [Fact]
    public async Task Email_registration_creates_a_new_profile_and_asks_for_a_phone_after_login()
    {
        await factory.EnsureCooperativeAsync();
        var email = $"trader-{NewCode()}@example.com";

        var registered = await RegisterAsync("Email", email);
        var tokens = await LoginTokensAsync(factory.CreateClient(), email.ToUpperInvariant());

        Assert.False(registered.GetProperty("oldDocumentsHidden").GetBoolean());
        Assert.True(tokens.GetProperty("phoneVerificationRequired").GetBoolean());
        Assert.Equal("Trader", tokens.GetProperty("subjectType").GetString());
    }

    [Fact]
    public async Task Identifier_with_an_account_cannot_register()
    {
        var existing = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);

        var start = await factory.CreateClient().PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = existing.PhoneNumber });

        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Equal("MSG-ACCOUNT-03", await start.ErrorCodeAsync());
    }

    [Fact]
    public async Task Invalid_identifier_password_or_token_creates_nothing()
    {
        await factory.EnsureCooperativeAsync();
        var client = factory.CreateClient();
        var phone = NewPhone();

        var badPhone = await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = "12345" });
        var token = await VerifiedTokenAsync(client, "Sms", phone);
        var weak = await client.PostAsJsonAsync("/api/auth/register", Body("Sms", phone, "short", token));
        var mismatch = await client.PostAsJsonAsync("/api/auth/register", new { channel = "Sms", identifier = phone, password = Password, passwordConfirmation = "Other123", verificationToken = token });
        var noToken = await client.PostAsJsonAsync("/api/auth/register", Body("Sms", NewPhone(), Password, "not-a-token"));
        var otherTarget = await client.PostAsJsonAsync("/api/auth/register", Body("Sms", NewPhone(), Password, token));

        Assert.Equal("MSG-GEN-01", await badPhone.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await weak.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await mismatch.ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-16", await noToken.ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-16", await otherTarget.ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.Accounts.AnyAsync(a => a.PhoneNumber == phone)));

        // The token was not consumed by the refused attempts.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", Body("Sms", phone, Password, token))).StatusCode);
    }

    private async Task<JsonElement> RegisterAsync(string channel, string identifier)
    {
        var client = factory.CreateClient();
        var token = await VerifiedTokenAsync(client, channel, identifier);
        var response = await client.PostAsJsonAsync("/api/auth/register", Body(channel, identifier, Password, token));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> VerifiedTokenAsync(HttpClient client, string channel, string identifier)
    {
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel, target = identifier }));
        var code = channel == "Sms" ? factory.LastSmsCode(identifier) : factory.LastEmailCode(identifier);
        return await VerifyAsync(client, id, code);
    }

    private static object Body(string channel, string identifier, string password, string token) =>
        new { channel, identifier, password, passwordConfirmation = password, verificationToken = token };
}
