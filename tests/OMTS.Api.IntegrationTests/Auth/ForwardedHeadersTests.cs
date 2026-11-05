using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Extensions;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>Behind a reverse proxy the per-IP limits must see the client, and only through a trusted proxy.</summary>
public class ForwardedHeadersTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string ProxyAddress = "10.0.0.5";

    [Fact]
    public async Task Clients_behind_a_trusted_proxy_get_their_own_login_bucket()
    {
        using var api = BehindProxy(trusted: true);
        var client = await StartedClientAsync(api);

        var sameClient = await Task.WhenAll(LoginFromAsync(client, "203.0.113.1"), LoginFromAsync(client, "203.0.113.1"), LoginFromAsync(client, "203.0.113.1"));
        var otherClient = await LoginFromAsync(client, "203.0.113.2");

        Assert.Single(sameClient, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
    }

    // From an unknown proxy the header is ignored: a client cannot pick a fresh bucket by sending it.
    [Fact]
    public async Task Forwarded_header_from_an_untrusted_address_is_ignored()
    {
        using var api = BehindProxy(trusted: false);
        var client = await StartedClientAsync(api);

        var answers = await Task.WhenAll(LoginFromAsync(client, "203.0.113.1"), LoginFromAsync(client, "203.0.113.2"), LoginFromAsync(client, "203.0.113.3"));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("198.51.100.7", "198.51.100.7")]
    public void Ipv6_clients_are_limited_per_64_block(string address, string key)
    {
        Assert.Equal(key, RateLimitingExtensions.ClientKey(IPAddress.Parse(address)));
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> BehindProxy(bool trusted) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("RateLimits:LoginsPerMinute", "2");
        b.UseSetting("ForwardedHeaders:KnownNetworks:0", trusted ? "10.0.0.0/8" : "192.168.0.0/16");
        b.ConfigureServices(s => s.AddTransient<IStartupFilter, ProxyConnectionStartupFilter>());
    });

    // The limit is per minute, and starting a host on a busy runner can take that long: the host starts before the first
    // login counts, and the logins go together, so they all fall in one window.
    private static async Task<HttpClient> StartedClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> LoginFromAsync(HttpClient client, string clientAddress)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { identifier = NewCode(), password = Password }),
        };
        request.Headers.Add("X-Forwarded-For", clientAddress);
        return client.SendAsync(request);
    }

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
