using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Messages;

namespace OMTS.Api.Extensions;

/// <summary>"RateLimits" section: requests per minute and client IP on the anonymous endpoints.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Endpoints that send a code or check one (registration, recovery, resend, verify).</summary>
    public int CodeRequestsPerMinute { get; set; } = 10;

    public int LoginsPerMinute { get; set; } = 30;
}

/// <summary>
/// Per-IP limits on top of the per-account limits of ACS-02/ACS-06, so one client cannot pump SMS to many
/// numbers or try many accounts. Behind a reverse proxy, configure forwarded headers so the client IP is seen.
/// </summary>
public static class RateLimitingExtensions
{
    public const string CodeRequests = "code-requests";
    public const string Logins = "logins";

    public static IServiceCollection AddOmtsRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.SectionName));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var message = UserMessages.TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { status = 429, title = message.Code, code = message.Code, detail = message.Text },
                    (System.Text.Json.JsonSerializerOptions?)null,
                    "application/problem+json",
                    cancellationToken);
            };
            options.AddPolicy(CodeRequests, context => PerClient(context, o => o.CodeRequestsPerMinute));
            options.AddPolicy(Logins, context => PerClient(context, o => o.LoginsPerMinute));
        });
        return services;
    }

    private static RateLimitPartition<string> PerClient(HttpContext context, Func<RateLimitOptions, int> permits)
    {
        var limit = permits(context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }
}
