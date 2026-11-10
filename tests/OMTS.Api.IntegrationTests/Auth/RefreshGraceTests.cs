using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>
/// The refresh token a rotation replaced stays valid for 30 seconds, repeatedly, for a client that never saw the rotation
/// response; every other old token is replay. The clock is moved by hand, the sessions are real.
/// </summary>
public class RefreshGraceTests
{
    private const string Password = "Secret123";
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Normal_rotation_returns_a_new_refresh_token_flagged_as_rotated()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());

        var refreshed = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var tokens = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tokens.GetProperty("refreshTokenRotated").GetBoolean());
        Assert.NotEqual(login.RefreshToken, tokens.GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task Previous_token_within_grace_gets_an_access_token_and_no_second_rotation()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());
        var start = DateTimeOffset.UtcNow;
        api.Clock.Set(start);
        var rotated = await (await RefreshAsync(client, login.RefreshToken)).Content.ReadFromJsonAsync<JsonElement>();
        var current = rotated.GetProperty("refreshToken").GetString()!;
        var hashBefore = await StoredRefreshHashAsync(api, login.AccountId);

        api.Clock.Set(start + Grace - TimeSpan.FromSeconds(1));
        var grace = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, grace.StatusCode);
        var tokens = await grace.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(tokens.GetProperty("refreshTokenRotated").GetBoolean());
        Assert.Equal(login.RefreshToken, tokens.GetProperty("refreshToken").GetString());
        Assert.Equal(rotated.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset(), tokens.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset());
        Assert.Equal(hashBefore, await StoredRefreshHashAsync(api, login.AccountId));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(client, tokens.GetProperty("accessToken").GetString()!)).StatusCode);

        // The token the winner got is still the current one: the session survived and was not rotated again.
        var next = await RefreshAsync(client, current);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.True((await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshTokenRotated").GetBoolean());
    }

    [Fact]
    public async Task Previous_token_after_grace_ends_the_session()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());
        var start = DateTimeOffset.UtcNow;
        api.Clock.Set(start);
        var rotated = await (await RefreshAsync(client, login.RefreshToken)).Content.ReadFromJsonAsync<JsonElement>();

        api.Clock.Set(start + Grace + TimeSpan.FromSeconds(1));
        var late = await RefreshAsync(client, login.RefreshToken);
        var successor = await RefreshAsync(client, rotated.GetProperty("refreshToken").GetString()!);

        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
        Assert.Equal("MSG19", await late.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);
        Assert.Equal("Refresh token reuse detected", (await StoredSessionAsync(api, login.AccountId)).RevokeReason);
    }

    // The rotation was committed but its response never reached the client, which keeps using the old token.
    // Nothing is stored to repair that: once the grace period is over the old token revokes the session, and the token
    // the client never received is dead with it. The web asks for a new login, the Mini App signs in again silently.
    [Fact]
    public async Task Lost_rotation_response_revokes_the_session_when_the_old_token_comes_back_after_grace()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());
        var start = DateTimeOffset.UtcNow;
        api.Clock.Set(start);
        var lostResponse = await (await RefreshAsync(client, login.RefreshToken)).Content.ReadFromJsonAsync<JsonElement>();

        api.Clock.Set(start + TimeSpan.FromMinutes(14));
        var retry = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        Assert.NotNull((await StoredSessionAsync(api, login.AccountId)).RevokedAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, lostResponse.GetProperty("refreshToken").GetString()!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(client, lostResponse.GetProperty("accessToken").GetString()!)).StatusCode);
    }

    // Only the token the last rotation replaced is forgiven, however recently the older ones were replaced.
    [Fact]
    public async Task Token_older_than_the_previous_one_ends_the_session_at_once()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());
        var second = await RefreshTokensAsync(client, login.RefreshToken);
        var third = await RefreshTokensAsync(client, second.GetProperty("refreshToken").GetString()!);

        var replay = await RefreshAsync(client, login.RefreshToken);
        var current = await RefreshAsync(client, third.GetProperty("refreshToken").GetString()!);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("MSG19", await replay.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);
        Assert.NotNull((await StoredSessionAsync(api, login.AccountId)).RevokedAt);
    }

    [Fact]
    public async Task Grace_is_refused_for_a_locked_account_and_ends_the_session()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());
        await RefreshAsync(client, login.RefreshToken);
        await api.UpdateAsync<Account>(login.AccountId, a => a.StatusId = StatusIds.Account.Locked);

        var grace = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.Forbidden, grace.StatusCode);
        Assert.Equal("MSG-ACCOUNT-07", await grace.ErrorCodeAsync());
        Assert.NotNull((await StoredSessionAsync(api, login.AccountId)).RevokedAt);
    }

    [Fact]
    public async Task Grace_is_refused_for_a_revoked_session()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());
        await RefreshAsync(client, login.RefreshToken);
        await api.UpdateAsync<LoginSession>((await StoredSessionAsync(api, login.AccountId)).Id, s => s.Revoke(DateTimeOffset.UtcNow, "Logout"));

        var grace = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, grace.StatusCode);
    }

    [Fact]
    public async Task Grace_is_refused_for_staff_without_an_effective_grant()
    {
        await using var api = new MovableClockApiFactory();
        var staff = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var grant = await api.GrantAsync(staff, StaffRole.ZoneManager, null);
        var client = api.CreateClient();
        var login = await LoginAsync(client, staff.Username);
        await RefreshAsync(client, login.RefreshToken);
        await api.UpdateAsync<AccessGrant>(grant.Id, g => g.StatusId = StatusIds.AccessGrant.Revoked);

        var grace = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.Forbidden, grace.StatusCode);
        Assert.NotNull((await StoredSessionAsync(api, staff.Id)).RevokedAt);
    }

    [Fact]
    public async Task Grace_is_refused_once_the_session_reached_its_absolute_expiry()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());
        var start = DateTimeOffset.UtcNow;
        await api.UpdateAsync<LoginSession>((await StoredSessionAsync(api, login.AccountId)).Id, s => s.AbsoluteExpiresAt = start + TimeSpan.FromSeconds(20));
        api.Clock.Set(start);
        await RefreshAsync(client, login.RefreshToken);

        api.Clock.Set(start + TimeSpan.FromSeconds(25));
        var grace = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, grace.StatusCode);
    }

    [Fact]
    public async Task Access_token_never_outlives_the_absolute_expiry_of_its_session()
    {
        await using var api = new MovableClockApiFactory();
        var (client, login) = await StartAsync(api, NewCode());
        var start = DateTimeOffset.UtcNow;
        var absoluteExpiry = start + TimeSpan.FromMinutes(5);
        await api.UpdateAsync<LoginSession>((await StoredSessionAsync(api, login.AccountId)).Id, s => s.AbsoluteExpiresAt = absoluteExpiry);
        api.Clock.Set(start);

        var rotated = await RefreshTokensAsync(client, login.RefreshToken);
        var grace = await (await RefreshAsync(client, login.RefreshToken)).Content.ReadFromJsonAsync<JsonElement>();

        foreach (var tokens in new[] { rotated, grace })
        {
            Assert.Equal(absoluteExpiry, tokens.GetProperty("accessTokenExpiresAt").GetDateTimeOffset());
            Assert.Equal(absoluteExpiry.ToUnixTimeSeconds(), ExpiryClaim(tokens.GetProperty("accessToken").GetString()!));
        }
    }

    private static long ExpiryClaim(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.GetProperty("exp").GetInt64();
    }

    private static async Task<(HttpClient Client, LoginResult Login)> StartAsync(MovableClockApiFactory api, string username)
    {
        var account = await api.AddAccountAsync(username, Password);
        var client = api.CreateClient();
        return (client, await LoginAsync(client, username, account.Id));
    }

    private static async Task<LoginResult> LoginAsync(HttpClient client, string username, Guid? accountId = null)
    {
        var tokens = await LoginTokensAsync(client, username, Password);
        return new LoginResult(accountId ?? tokens.GetProperty("accountId").GetGuid(), tokens.GetProperty("refreshToken").GetString()!);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

    private static async Task<JsonElement> RefreshTokensAsync(HttpClient client, string refreshToken)
    {
        var response = await RefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> GetMeAsync(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request);
    }

    private static Task<LoginSession> StoredSessionAsync(AuthApiFactory api, Guid accountId) =>
        api.QueryAsync(db => db.LoginSessions.AsNoTracking().SingleAsync(s => s.AccountId == accountId));

    private static async Task<string> StoredRefreshHashAsync(AuthApiFactory api, Guid accountId) =>
        (await StoredSessionAsync(api, accountId)).RefreshTokenHash;

    private sealed record LoginResult(Guid AccountId, string RefreshToken);
}
