using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Auth;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>QA checks of the OMTS-208 review fixes: boundary input, tampered refresh tokens, counter bounds.</summary>
public class ReviewFixQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "Secret123";

    [Theory]
    [InlineData("{\"identifier\":null,\"password\":\"Secret123\"}")]
    [InlineData("{\"identifier\":\"qa-nobody\",\"password\":null}")]
    [InlineData("{\"identifier\":123,\"password\":\"Secret123\"}")]
    [InlineData("{}")]
    [InlineData("not json")]
    public async Task Malformed_login_bodies_are_refused_with_400(string body)
    {
        var response = await factory.CreateClient().PostAsync("/api/auth/login",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // [Required] rejects whitespace-only strings, so they never reach the lookup.
    [Fact]
    public async Task Whitespace_only_identifier_is_refused_with_400()
    {
        var response = await PostLoginAsync(factory.CreateClient(), "   ", Password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("\u0000")]
    [InlineData("'; DROP TABLE Accounts; --")]
    public async Task Control_or_sql_like_identifiers_get_the_normal_refusal(string identifier)
    {
        var response = await PostLoginAsync(factory.CreateClient(), identifier, Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(513, HttpStatusCode.BadRequest)]
    [InlineData(512, HttpStatusCode.Unauthorized)]
    public async Task Refresh_token_length_limit_is_enforced_at_the_boundary(int length, HttpStatusCode expected)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = new string('a', length) });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Missing_refresh_token_is_refused_with_400()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("..")]
    [InlineData("a.b.c")]
    [InlineData("00000000000000000000000000000000.x.y")]
    public async Task Malformed_refresh_tokens_get_401_not_500(string token)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Right session id and right MAC, but the random part was edited: the MAC no longer covers it.
    [Fact]
    public async Task Genuine_mac_on_a_tampered_random_part_does_not_end_the_session()
    {
        await factory.AddAccountAsync("qa-tamper", Password);
        var client = factory.CreateClient();
        var refreshToken = await LoginRefreshTokenAsync(client, "qa-tamper");
        var parts = refreshToken.Split('.');
        var tampered = $"{parts[0]}.{new string(parts[1].Select(c => c == 'A' ? 'B' : 'A').ToArray())}.{parts[2]}";

        var forged = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tampered });
        var genuine = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
    }

    // Attacker owns session B and grafts the MAC of B's real token onto victim session A's id.
    [Fact]
    public async Task Mac_taken_from_another_sessions_token_does_not_end_the_victim_session()
    {
        await factory.AddAccountAsync("qa-victim", Password);
        await factory.AddAccountAsync("qa-attacker", Password);
        var victimClient = factory.CreateClient();
        var attackerClient = factory.CreateClient();
        var victimToken = await LoginRefreshTokenAsync(victimClient, "qa-victim");
        var attackerToken = await LoginRefreshTokenAsync(attackerClient, "qa-attacker");
        var victimParts = victimToken.Split('.');
        var attackerParts = attackerToken.Split('.');
        var grafted = $"{victimParts[0]}.{attackerParts[1]}.{attackerParts[2]}";

        var forged = await victimClient.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = grafted });
        var genuine = await victimClient.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = victimToken });

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
    }

    // The pre-fix token shape "{sessionId}.{random}" had no MAC: it must not end the session either.
    [Fact]
    public async Task Pre_fix_token_shape_without_mac_does_not_end_the_session()
    {
        await factory.AddAccountAsync("qa-oldshape", Password);
        var client = factory.CreateClient();
        var refreshToken = await LoginRefreshTokenAsync(client, "qa-oldshape");
        var parts = refreshToken.Split('.');
        var oldShape = $"{parts[0]}.{parts[1]}";

        var forged = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = oldShape });
        var genuine = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
    }

    // Login with a known identifier and a wrong password for one account, several at a time.
    [Fact]
    public async Task Sequential_wrong_passwords_lock_on_the_fifth_and_answer_423_after()
    {
        await factory.AddAccountAsync("qa-sequential", Password);
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            statuses.Add((await PostLoginAsync(client, "qa-sequential", "Wrong123")).StatusCode);
        }

        Assert.Equal(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Locked, HttpStatusCode.Locked },
            statuses);
    }

    private static async Task<string> LoginRefreshTokenAsync(HttpClient client, string identifier)
    {
        var response = await PostLoginAsync(client, identifier, Password);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString()!;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string identifier, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { identifier, password });
}
