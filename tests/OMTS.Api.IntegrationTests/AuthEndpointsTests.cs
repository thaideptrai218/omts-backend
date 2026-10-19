using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Domain.Enums;
using OMTS.Domain.Statuses;

namespace OMTS.Api.IntegrationTests;

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
        Assert.Equal("MSG12", await ErrorCodeAsync(wrongPassword));
        Assert.Equal("MSG12", await ErrorCodeAsync(unknownUser));
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
        Assert.Equal("MSG69", await ErrorCodeAsync(correct));
    }

    // UC02 – 2b.
    [Fact]
    public async Task Locked_account_and_staff_without_access_cannot_log_in()
    {
        await factory.AddAccountAsync("locked1", Password, configure: a => a.StatusId = StatusIds.Account.Locked);
        await factory.AddAccountAsync("staff1", Password, SubjectType.Staff);
        var client = factory.CreateClient();

        Assert.Equal("MSG13", await ErrorCodeAsync(await PostLoginAsync(client, "locked1", Password)));
        Assert.Equal("MSG13", await ErrorCodeAsync(await PostLoginAsync(client, "staff1", Password)));
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

        Assert.Equal("MSG68", await ErrorCodeAsync(response));
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
