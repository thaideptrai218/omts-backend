using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Auth;

public class AuthEndpointsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "Secret123";

    [Fact]
    public async Task Login_returns_tokens_that_authorize_requests()
    {
        await factory.AddAccountAsync("owner1", Password);
        var client = factory.CreateClient();

        var tokens = await LoginAsync(client, "owner1", Password);

        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("refreshToken").GetString()));
        var me = await SendAsync(client, HttpMethod.Get, "/api/me", tokens);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_requires_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // UC02 – 2a: the message must not reveal whether the identifier exists.
    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_generic_error()
    {
        await factory.AddAccountAsync("owner2", Password);
        var client = factory.CreateClient();

        var wrongPassword = await PostLoginAsync(client, "owner2", "Wrong123");
        var unknownUser = await PostLoginAsync(client, "nobody", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownUser.Content.ReadAsStringAsync());
    }

    // Problem shape: title and code are the SRS 27/10 code; legacyCode is the old code, for one release, only when it changed.
    [Fact]
    public async Task Problems_carry_the_srs_code_and_the_legacy_code_for_one_release()
    {
        var client = factory.CreateClient();

        var problem = await (await PostLoginAsync(client, $"ghost-{Guid.NewGuid():N}", "Wrong123")).Content.ReadFromJsonAsync<JsonElement>();
        var unreadable = await (await client.PostAsJsonAsync("/api/auth/login", new { identifier = 5, password = Password })).Content.ReadFromJsonAsync<JsonElement>();
        var kept = await (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "not-a-refresh-token" })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal("MSG-ACCOUNT-05", problem.GetProperty("title").GetString());
        Assert.Equal("MSG-ACCOUNT-05", problem.GetProperty("code").GetString());
        Assert.Equal("MSG12", problem.GetProperty("legacyCode").GetString());
        Assert.Equal("The login information is incorrect. Please check it and try again.", problem.GetProperty("detail").GetString());
        Assert.Equal("MSG-GEN-01", unreadable.GetProperty("code").GetString());
        Assert.Equal("MSG01", unreadable.GetProperty("legacyCode").GetString());
        Assert.True(unreadable.TryGetProperty("field", out _));
        Assert.Equal("MSG19", kept.GetProperty("code").GetString());
        Assert.False(kept.TryGetProperty("legacyCode", out _));
    }

    // An unknown identifier is locked out like an account, so MSG-ACCOUNT-06 does not reveal which identifiers exist.
    [Fact]
    public async Task Fifth_failure_on_an_unknown_identifier_also_answers_MSG_ACCOUNT_06()
    {
        var client = factory.CreateClient();
        var identifier = $"ghost-{Guid.NewGuid():N}";

        for (var attempt = 1; attempt < 5; attempt++)
        {
            Assert.Equal("MSG-ACCOUNT-05", await ErrorCodeAsync(await PostLoginAsync(client, identifier, "Wrong123")));
        }

        var fifth = await PostLoginAsync(client, identifier, "Wrong123");
        Assert.Equal(HttpStatusCode.Locked, fifth.StatusCode);
        var problem = await fifth.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MSG-ACCOUNT-06", problem.GetProperty("code").GetString());
        Assert.Equal("MSG69", problem.GetProperty("legacyCode").GetString());
        Assert.Equal("Too many failed login attempts. Please try again after 15 minutes.", problem.GetProperty("detail").GetString());
    }

    // UC02 – 2e: the 5th failure locks for 15 minutes, even with the right password afterwards.
    [Fact]
    public async Task Fifth_failed_login_locks_the_account_temporarily()
    {
        await factory.AddAccountAsync("owner3", Password);
        var client = factory.CreateClient();

        for (var attempt = 1; attempt < 5; attempt++)
        {
            await PostLoginAsync(client, "owner3", "Wrong123");
        }

        var fifth = await PostLoginAsync(client, "owner3", "Wrong123");
        var correct = await PostLoginAsync(client, "owner3", Password);

        Assert.Equal(HttpStatusCode.Locked, fifth.StatusCode);
        Assert.Equal("MSG-ACCOUNT-06", await ErrorCodeAsync(correct));
    }

    // UC02 – 2b.
    [Fact]
    public async Task Locked_account_and_staff_without_access_cannot_log_in()
    {
        await factory.AddAccountAsync("locked1", Password, configure: a => a.StatusId = StatusIds.Account.Locked);
        await factory.AddAccountAsync("staff1", Password, SubjectType.Staff);
        var client = factory.CreateClient();

        Assert.Equal("MSG-ACCOUNT-07", await ErrorCodeAsync(await PostLoginAsync(client, "locked1", Password)));
        Assert.Equal("MSG-ACCOUNT-07", await ErrorCodeAsync(await PostLoginAsync(client, "staff1", Password)));
    }

    // UC02 – 2d.
    [Fact]
    public async Task Temporary_password_older_than_24_hours_is_refused()
    {
        await factory.AddAccountAsync("temp1", Password, configure: a =>
        {
            a.MustChangePassword = true;
            a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddHours(-25);
        });

        var response = await PostLoginAsync(factory.CreateClient(), "temp1", Password);

        Assert.Equal("MSG-ACCOUNT-08", await ErrorCodeAsync(response));
    }

    // ACS-02: a valid temporary password logs in, but only to change it.
    [Fact]
    public async Task Valid_temporary_password_logs_in_with_must_change_password()
    {
        await factory.AddAccountAsync("temp2", Password, configure: a =>
        {
            a.MustChangePassword = true;
            a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddHours(-23);
        });

        var tokens = await LoginAsync(factory.CreateClient(), "temp2", Password);

        Assert.True(tokens.GetProperty("mustChangePassword").GetBoolean());
    }

    // Clients map the code, but the detail is the SRS 27/10 wording (MSG-ACCOUNT-05, MSG-ACCOUNT-08).
    [Fact]
    public async Task Login_errors_carry_the_srs_message_text()
    {
        await factory.AddAccountAsync("temp3", Password, configure: a =>
        {
            a.MustChangePassword = true;
            a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddHours(-25);
        });
        var client = factory.CreateClient();

        var unknown = await (await PostLoginAsync(client, "nobody", Password)).Content.ReadFromJsonAsync<JsonElement>();
        var expired = await (await PostLoginAsync(client, "temp3", Password)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("The login information is incorrect. Please check it and try again.", unknown.GetProperty("detail").GetString());
        Assert.Equal("MSG-ACCOUNT-08", expired.GetProperty("code").GetString());
        Assert.Equal("MSG68", expired.GetProperty("legacyCode").GetString());
        Assert.Equal("Your temporary password has expired. Please ask the Cooperative Owner for a new one.", expired.GetProperty("detail").GetString());
    }

    // ACS-02: a locked account cannot keep its session alive through refresh.
    [Fact]
    public async Task Refresh_is_refused_once_the_account_is_locked()
    {
        var account = await factory.AddAccountAsync("owner6", Password);
        var client = factory.CreateClient();
        var tokens = await LoginAsync(client, "owner6", Password);

        await factory.UpdateAsync<Domain.Entities.Identity.Account>(account.Id, a => a.StatusId = StatusIds.Account.Locked);
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });

        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        Assert.Equal("MSG-ACCOUNT-07", await ErrorCodeAsync(refresh));
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_replaying_the_old_one_ends_the_session()
    {
        await factory.AddAccountAsync("owner4", Password);
        var client = factory.CreateClient();
        var first = await LoginAsync(client, "owner4", Password);
        var oldRefreshToken = first.GetProperty("refreshToken").GetString();

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = oldRefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(oldRefreshToken, second.GetProperty("refreshToken").GetString());

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = oldRefreshToken });
        Assert.Equal("MSG19", await ErrorCodeAsync(replay));

        // Reuse revoked the whole session, so the newest tokens are dead too.
        var newRefresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = second.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, newRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/api/me", second)).StatusCode);
    }

    // Any older token of the session (not just the previous one) is reuse and ends the session.
    [Fact]
    public async Task Replaying_a_token_two_rotations_old_ends_the_session()
    {
        await factory.AddAccountAsync("owner7", Password);
        var client = factory.CreateClient();
        var first = await LoginAsync(client, "owner7", Password);
        var second = await RefreshAsync(client, first);
        var third = await RefreshAsync(client, second);

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.GetProperty("refreshToken").GetString() });
        var current = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = third.GetProperty("refreshToken").GetString() });

        Assert.Equal("MSG19", await ErrorCodeAsync(replay));
        Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/api/me", third)).StatusCode);
    }

    // A session opened with a temporary password cannot outlive the password's 24 hours through refresh.
    [Fact]
    public async Task Refresh_is_refused_once_the_temporary_password_has_expired()
    {
        var account = await factory.AddAccountAsync("temp4", Password, configure: a =>
        {
            a.MustChangePassword = true;
            a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddHours(-23);
        });
        var client = factory.CreateClient();
        var tokens = await LoginAsync(client, "temp4", Password);

        await factory.UpdateAsync<Domain.Entities.Identity.Account>(account.Id, a => a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddHours(-25));
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });

        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        Assert.Equal("MSG-ACCOUNT-08", await ErrorCodeAsync(refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/api/me", tokens)).StatusCode);
    }

    // NFR-01: a revoked session cannot be reused, even before the access token expires.
    [Fact]
    public async Task Logout_revokes_access_and_refresh_tokens()
    {
        await factory.AddAccountAsync("owner5", Password);
        var client = factory.CreateClient();
        var tokens = await LoginAsync(client, "owner5", Password);

        var logout = await SendAsync(client, HttpMethod.Post, "/api/auth/logout", tokens);
        var me = await SendAsync(client, HttpMethod.Get, "/api/me", tokens);
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string identifier, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { identifier, password });

    private static async Task<JsonElement> LoginAsync(HttpClient client, string identifier, string password)
    {
        var response = await PostLoginAsync(client, identifier, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> RefreshAsync(HttpClient client, JsonElement tokens)
    {
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, JsonElement tokens)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client.SendAsync(request);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("code").GetString();
    }
}
