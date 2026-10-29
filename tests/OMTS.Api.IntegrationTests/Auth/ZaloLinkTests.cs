using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>
/// Zalo Mini App "Liên kết tài khoản": the Zalo phone number finds the account, the Zalo user id is kept on it.
/// The test host uses the fake Zalo Open API: "fake:&lt;id&gt;" access tokens, "fake:&lt;number&gt;" phone tokens.
/// </summary>
public class ZaloLinkTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task New_staff_links_by_zalo_phone_without_password_or_phone_steps()
    {
        var staff = await AddStaffAsync(a =>
        {
            a.PhoneVerifiedAt = null;
            a.MustChangePassword = true;
            // Even an expired temporary password does not block linking: mobile has no password.
            a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddDays(-2);
        });
        var zaloId = NewCode();

        // Zalo writes the number as 84xxxxxxxxx.
        var response = await LinkAsync(zaloId, "84" + staff.PhoneNumber![1..]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("mustChangePassword").GetBoolean());
        Assert.False(body.GetProperty("phoneVerificationRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("notice").ValueKind);

        // No gate: the session reaches business endpoints right away.
        Assert.Equal(HttpStatusCode.OK, (await Authorized(body).GetAsync("/api/me/permissions")).StatusCode);

        var saved = await factory.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == staff.Id));
        Assert.Equal(zaloId, saved.ZaloAccountId);
        Assert.NotNull(saved.PhoneVerifiedAt);
        Assert.Null(saved.TemporaryPasswordIssuedAt);

        // The temporary password the Owner handed out stops working.
        var oldPassword = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Password });
        Assert.Equal("MSG12", await oldPassword.ErrorCodeAsync());
    }

    [Fact]
    public async Task Trader_with_several_old_customer_profiles_gets_msg57()
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader, a => a.PhoneVerifiedAt = null);
        await factory.UseDbAsync(db =>
        {
            db.Customers.Add(new Customer { CooperativeId = cooperative.Id, AccountId = trader.Id, Name = "Own" });
            db.Customers.AddRange(Enumerable.Range(0, 2).Select(i => new Customer { CooperativeId = cooperative.Id, Name = $"Offline {i}", PhoneNumber = trader.PhoneNumber }));
        });

        var body = await (await LinkAsync(NewCode(), trader.PhoneNumber!)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("oldDocumentsHidden").GetBoolean());
        Assert.Equal("Old documents are temporarily hidden until identity verification is complete.", body.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task Unknown_number_creates_nothing_and_shows_it_masked()
    {
        var phone = NewPhone();

        var response = await LinkAsync(NewCode(), phone);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ZALO_PHONE_NOT_FOUND", await response.ErrorCodeAsync());
        var masked = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("maskedPhone").GetString();
        Assert.StartsWith(phone[..2], masked);
        Assert.DoesNotContain(phone, masked);
        Assert.False(await factory.QueryAsync(db => db.Accounts.AnyAsync(a => a.PhoneNumber == phone)));
    }

    [Fact]
    public async Task A_number_that_is_not_vietnamese_is_refused()
    {
        var response = await LinkAsync(NewCode(), "6591234567");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ZALO_PHONE_UNSUPPORTED", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Locked_account_and_staff_without_access_cannot_link()
    {
        var locked = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader, a => a.StatusId = StatusIds.Account.Locked);
        var noGrant = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        Assert.Equal("MSG13", await (await LinkAsync(NewCode(), locked.PhoneNumber!)).ErrorCodeAsync());
        Assert.Equal("MSG13", await (await LinkAsync(NewCode(), noGrant.PhoneNumber!)).ErrorCodeAsync());
    }

    [Fact]
    public async Task A_zalo_user_links_one_account_only()
    {
        var first = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var second = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var zaloId = NewCode();

        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(zaloId, first.PhoneNumber!)).StatusCode);
        var again = await LinkAsync(zaloId, first.PhoneNumber!);
        var other = await LinkAsync(zaloId, second.PhoneNumber!);

        // Linking the same pair again only opens a session.
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("ZALO_ALREADY_LINKED", await other.ErrorCodeAsync());
    }

    [Fact]
    public async Task Linked_zalo_user_gets_a_session_back_until_unlinked()
    {
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var zaloId = NewCode();
        var client = factory.CreateClient();

        var notLinked = await client.PostAsJsonAsync("/api/auth/zalo/login", new { zaloAccessToken = Fake(zaloId) });
        var linked = await (await LinkAsync(zaloId, trader.PhoneNumber!)).Content.ReadFromJsonAsync<JsonElement>();
        var login = await client.PostAsJsonAsync("/api/auth/zalo/login", new { zaloAccessToken = Fake(zaloId) });

        Assert.Equal("ZALO_NOT_LINKED", await notLinked.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(trader.Id, (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accountId").GetGuid());

        var linkedClient = Authorized(linked);
        Assert.Equal(HttpStatusCode.NoContent, (await linkedClient.DeleteAsync("/api/me/zalo-link")).StatusCode);

        // The account stays, the link and the current session do not.
        Assert.Equal("ZALO_NOT_LINKED", await (await client.PostAsJsonAsync("/api/auth/zalo/login", new { zaloAccessToken = Fake(zaloId) })).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await linkedClient.GetAsync("/api/me")).StatusCode);
        Assert.Null(await factory.QueryAsync(db => db.Accounts.Where(a => a.Id == trader.Id).Select(a => a.ZaloAccountId).SingleAsync()));
    }

    [Fact]
    public async Task Tokens_zalo_refuses_are_reported_separately()
    {
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var client = factory.CreateClient();

        var badAccess = await client.PostAsJsonAsync("/api/auth/zalo/link", new { zaloAccessToken = "forged", phoneToken = Fake(trader.PhoneNumber!) });
        var badPhone = await client.PostAsJsonAsync("/api/auth/zalo/link", new { zaloAccessToken = Fake(NewCode()), phoneToken = "forged" });

        Assert.Equal(HttpStatusCode.Unauthorized, badAccess.StatusCode);
        Assert.Equal("ZALO_TOKEN_INVALID", await badAccess.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, badPhone.StatusCode);
        Assert.Equal("ZALO_PHONE_TOKEN_INVALID", await badPhone.ErrorCodeAsync());
    }

    [Fact]
    public async Task Without_a_zalo_provider_linking_answers_msg04()
    {
        await using var notConfigured = factory.WithWebHostBuilder(b => b.UseSetting("Zalo:Provider", ""));
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);

        var response = await notConfigured.CreateClient().PostAsJsonAsync(
            "/api/auth/zalo/link", new { zaloAccessToken = Fake(NewCode()), phoneToken = Fake(trader.PhoneNumber!) });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("MSG04", await response.ErrorCodeAsync());
    }

    private static string Fake(string value) => "fake:" + value;

    private Task<HttpResponseMessage> LinkAsync(string zaloId, string phone) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/zalo/link", new
        {
            zaloAccessToken = Fake(zaloId),
            phoneToken = Fake(phone),
            clientType = "Mobile",
            deviceName = "Zalo Mini App test",
        });

    private HttpClient Authorized(JsonElement tokens)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>A staff account with an effective grant, so it may log in (ACS-02).</summary>
    private async Task<Account> AddStaffAsync(Action<Account> configure)
    {
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, configure);
        var role = await factory.AddRoleAsync($"Role {NewCode()}");
        await factory.GrantAsync(staff, role, null);
        return staff;
    }
}
