using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Extensions;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>
/// The Next.js server may name the end user's address and browser, but only with the secret it shares with the API. Without
/// it the headers are ignored, so a client cannot pick its own rate-limit bucket.
/// </summary>
public class BffClientHeadersTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string ProxyAddress = "10.0.0.5";
    private const string Password = "Secret123";

    // Random per run, so no secret-like literal is committed.
    private readonly string _secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // The limit is per minute; sent together, the requests take their permit in one window.
    [Fact]
    public async Task With_the_secret_each_supplied_address_gets_its_own_login_bucket()
    {
        using var api = Bff();
        var client = api.CreateClient();

        var sameClient = await Task.WhenAll(LoginFromAsync(client, "203.0.113.1", _secret), LoginFromAsync(client, "203.0.113.1", _secret), LoginFromAsync(client, "203.0.113.1", _secret));
        var otherClient = await LoginFromAsync(client, "203.0.113.2", _secret);

        Assert.Single(sameClient, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-secret")]
    [InlineData("")]
    public async Task Without_a_valid_secret_the_client_headers_are_ignored(string? presented)
    {
        using var api = Bff();
        var client = api.CreateClient();

        var answers = await Task.WhenAll(LoginFromAsync(client, "203.0.113.1", presented), LoginFromAsync(client, "203.0.113.2", presented), LoginFromAsync(client, "203.0.113.3", presented));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task When_the_feature_is_off_even_the_right_secret_changes_nothing()
    {
        using var api = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:LoginsPerMinute", "2");
            b.UseSetting("BffClient:SharedSecret", _secret);
        });
        var client = api.CreateClient();

        var answers = await Task.WhenAll(LoginFromAsync(client, "203.0.113.1", _secret), LoginFromAsync(client, "203.0.113.2", _secret), LoginFromAsync(client, "203.0.113.3", _secret));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    // An enabled feature with no secret can never match: an empty presented secret is no secret.
    [Fact]
    public async Task With_no_secret_configured_nothing_is_trusted()
    {
        using var api = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:LoginsPerMinute", "2");
            b.UseSetting("BffClient:Enabled", "true");
        });
        var client = api.CreateClient();

        var answers = await Task.WhenAll(LoginFromAsync(client, "203.0.113.1", ""), LoginFromAsync(client, "203.0.113.2", ""), LoginFromAsync(client, "203.0.113.3", ""));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    // A value that is no single, complete address falls back to the connection's own address.
    [Fact]
    public async Task A_malformed_client_address_is_ignored_even_with_the_secret()
    {
        using var api = Bff();
        var client = api.CreateClient();

        var answers = await Task.WhenAll(LoginFromAsync(client, "1", _secret), LoginFromAsync(client, "127.1", _secret), LoginFromAsync(client, "203.0.113.1, 203.0.113.2", _secret));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Supplied_address_and_user_agent_are_stored_on_the_session()
    {
        using var api = Bff();
        var account = await factory.AddAccountAsync(NewCode(), Password);

        var request = LoginRequest(account.Username, "203.0.113.7", _secret);
        request.Headers.Add(BffClientHeadersSetup.UserAgentHeader, "Mozilla/5.0 (OMTS test browser)");
        request.Headers.UserAgent.ParseAdd("node");
        var response = await api.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await factory.QueryAsync(db => db.LoginSessions.AsNoTracking().SingleAsync(s => s.AccountId == account.Id));
        Assert.Equal("203.0.113.7", session.IpAddress);
        Assert.Equal("Mozilla/5.0 (OMTS test browser)", session.UserAgent);
    }

    [Fact]
    public async Task Without_the_secret_the_session_keeps_what_the_connection_says()
    {
        using var api = Bff();
        var account = await factory.AddAccountAsync(NewCode(), Password);

        var request = LoginRequest(account.Username, "203.0.113.7", "wrong");
        request.Headers.Add(BffClientHeadersSetup.UserAgentHeader, "Forged browser");
        request.Headers.UserAgent.ParseAdd("node");
        await api.CreateClient().SendAsync(request);

        var session = await factory.QueryAsync(db => db.LoginSessions.AsNoTracking().SingleAsync(s => s.AccountId == account.Id));
        Assert.Null(session.IpAddress);
        Assert.Equal("node", session.UserAgent);
    }

    // Behind a trusted proxy X-Forwarded-For names the BFF's own caller at best; the BFF's word about the end user wins.
    [Fact]
    public async Task Supplied_address_wins_over_forwarded_for_from_a_trusted_proxy()
    {
        using var api = Bff(trustedProxy: true);
        var account = await factory.AddAccountAsync(NewCode(), Password);

        var request = LoginRequest(account.Username, "203.0.113.7", _secret);
        request.Headers.Add("X-Forwarded-For", "198.51.100.9");
        await api.CreateClient().SendAsync(request);

        var session = await factory.QueryAsync(db => db.LoginSessions.AsNoTracking().SingleAsync(s => s.AccountId == account.Id));
        Assert.Equal("203.0.113.7", session.IpAddress);
    }

    [Fact]
    public async Task Forwarded_for_still_applies_to_requests_that_do_not_come_from_the_BFF()
    {
        using var api = Bff(trustedProxy: true);
        var account = await factory.AddAccountAsync(NewCode(), Password);

        var request = LoginRequest(account.Username, "203.0.113.7", "wrong");
        request.Headers.Add("X-Forwarded-For", "198.51.100.9");
        await api.CreateClient().SendAsync(request);

        var session = await factory.QueryAsync(db => db.LoginSessions.AsNoTracking().SingleAsync(s => s.AccountId == account.Id));
        Assert.Equal("198.51.100.9", session.IpAddress);
    }

    [Theory]
    [InlineData(false, false, "", true)]
    [InlineData(true, true, "", true)]
    [InlineData(true, false, "", false)]
    [InlineData(true, false, "too-short", false)]
    [InlineData(true, false, "0123456789abcdef0123456789abcdef", true)]
    public void Secret_is_required_outside_development_when_the_feature_is_on(bool enabled, bool development, string secret, bool valid)
    {
        var options = new BffClientOptions { Enabled = enabled, SharedSecret = secret };

        Assert.Equal(valid, options.IsValid(development));
    }

    private WebApplicationFactory<Program> Bff(bool trustedProxy = false) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("RateLimits:LoginsPerMinute", "2");
        b.UseSetting("BffClient:Enabled", "true");
        b.UseSetting("BffClient:SharedSecret", _secret);
        if (trustedProxy)
        {
            b.UseSetting("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8");
            b.ConfigureServices(s => s.AddTransient<IStartupFilter, ProxyConnectionStartupFilter>());
        }
    });

    private static HttpRequestMessage LoginRequest(string identifier, string clientAddress, string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { identifier, password = Password }),
        };
        request.Headers.Add(BffClientHeadersSetup.ClientIpHeader, clientAddress);
        if (secret is not null)
        {
            request.Headers.Add(BffClientHeadersSetup.SecretHeader, secret);
        }

        return request;
    }

    private static Task<HttpResponseMessage> LoginFromAsync(HttpClient client, string clientAddress, string? secret) =>
        client.SendAsync(LoginRequest(NewCode(), clientAddress, secret));

    // The test server has no connection address; every request comes "from the proxy".
    private sealed class ProxyConnectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ProxyAddress);
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
