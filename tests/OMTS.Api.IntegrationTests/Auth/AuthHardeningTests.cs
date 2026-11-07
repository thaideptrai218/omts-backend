using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>Review follow-ups of the login endpoint: input limits, the temporary lock, forged refresh tokens.</summary>
public class AuthHardeningTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "Secret123";

    // Anonymous input is bounded before it is hashed, queried or counted.
    [Theory]
    [InlineData(255, 9)]
    [InlineData(8, 129)]
    public async Task Overlong_identifier_or_password_is_refused_with_400(int identifierLength, int passwordLength)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { identifier = new string('a', identifierLength), password = new string('1', passwordLength) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_identifier_is_refused_with_400()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { password = Password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Longest_allowed_identifier_and_password_still_get_the_normal_answer()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { identifier = new string('a', 254), password = new string('1', 128) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // UC02 – 2e: the lock is temporary; the account stays Active and logs in again once it lifts.
    [Fact]
    public async Task Account_stays_active_while_locked_and_logs_in_after_the_lock_lifts()
    {
        var account = await factory.AddAccountAsync("lock-lift", Password);
        var client = factory.CreateClient();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await PostLoginAsync(client, "lock-lift", "Wrong123");
        }

        Assert.Equal(HttpStatusCode.Locked, (await PostLoginAsync(client, "lock-lift", Password)).StatusCode);
        var statusWhileLocked = 0;
        DateTimeOffset? lockoutEnd = null;
        await factory.UpdateAsync<Account>(account.Id, a =>
        {
            statusWhileLocked = a.StatusId;
            lockoutEnd = a.LockoutEndAt;
            a.LockoutEndAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        });

        Assert.Equal(StatusIds.Account.Active, statusWhileLocked);
        Assert.InRange(lockoutEnd!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, "lock-lift", Password)).StatusCode);
    }

    // A made-up token that names a real session (the id is in every access token) must not end that session.
    [Fact]
    public async Task Forged_refresh_token_naming_a_session_does_not_end_it()
    {
        await factory.AddAccountAsync("forge-target", Password);
        var client = factory.CreateClient();
        var login = await PostLoginAsync(client, "forge-target", Password);
        var refreshToken = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString()!;
        var sessionId = refreshToken[..refreshToken.IndexOf('.')];

        var forged = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = $"{sessionId}.{new string('x', 86)}.{new string('y', 43)}" });
        var genuine = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string identifier, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { identifier, password });
}
