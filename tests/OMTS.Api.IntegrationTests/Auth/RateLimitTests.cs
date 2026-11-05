using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>
/// Per-IP limits on the anonymous login and code endpoints, on top of the per-account ACS-02/ACS-06 limits. The requests go
/// together: one after another they can take longer than the window on a busy machine, and the last one then has a fresh window.
/// </summary>
public class RateLimitTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Logins_over_the_per_minute_limit_get_429()
    {
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:LoginsPerMinute", "2"));
        var client = api.CreateClient();

        var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync("/api/auth/login", new { identifier = NewCode(), password = Password })));

        Assert.Equal(2, answers.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
        Assert.Equal("60", refused.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task Code_requests_over_the_per_minute_limit_get_429_without_sending()
    {
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:CodeRequestsPerMinute", "1"));
        var client = api.CreateClient();
        var phones = new[] { NewPhone(), NewPhone() };

        var answers = await Task.WhenAll(phones.Select(phone => client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone })));

        Assert.Single(answers, r => r.IsSuccessStatusCode);
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
    }
}
