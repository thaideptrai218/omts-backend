using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>Per-IP limits on the anonymous login and code endpoints, on top of the per-account ACS-02/ACS-06 limits.</summary>
public class RateLimitTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Logins_over_the_per_minute_limit_get_429()
    {
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:LoginsPerMinute", "2"));
        var client = api.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/auth/login", new { identifier = NewCode(), password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var refused = await client.PostAsJsonAsync("/api/auth/login", new { identifier = NewCode(), password = Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
        Assert.Equal("60", refused.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task Code_requests_over_the_per_minute_limit_get_429_without_sending()
    {
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:CodeRequestsPerMinute", "1"));
        var client = api.CreateClient();
        var phones = new[] { NewPhone(), NewPhone() };

        Assert.True((await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phones[0] })).IsSuccessStatusCode);
        var refused = await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phones[1] });

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
    }
}
