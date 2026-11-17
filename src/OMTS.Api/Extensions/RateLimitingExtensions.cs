using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Messages;

namespace OMTS.Api.Extensions;

/// <summary>"RateLimits" section: requests per minute and client IP on the anonymous endpoints.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Endpoints that send a code or check one (registration, recovery, resend, verify).</summary>
    public int CodeRequestsPerMinute { get; set; } = 10;

    public int LoginsPerMinute { get; set; } = 30;

    /// <summary>The employee app login (LABOR-06), per client IP and apart from the staff and Zalo logins: a crew on one farm Wi-Fi must not compete with the Owner's login.</summary>
    public int EmployeeAppLoginsPerMinute { get; set; } = 30;

    /// <summary>
    /// Issuing employee login codes (LABOR-03), per signed-in account and not per IP: a Zone Manager onboarding a crew issues one code per
    /// worker, and the anonymous code endpoints must not share the budget. 60 a minute is one a second, more than a person can hand out.
    /// </summary>
    public int EmployeeLoginCodesPerMinute { get; set; } = 60;

    /// <summary>The bank notification webhook (API-13): the service's own servers, so a high limit that still stops a flood of unsigned requests.</summary>
    public int PaymentNotificationsPerMinute { get; set; } = 600;

    /// <summary>The public provenance page (ORIGIN-01): a page opened by hand needs a few, a client trying QR codes many.</summary>
    public int ProvenancePagesPerMinute { get; set; } = 60;
}

/// <summary>
/// Per-IP limits on top of the per-account limits of ACS-02/ACS-06, so one client cannot pump SMS to many
/// numbers or try many accounts. Behind a reverse proxy, list it in <c>ForwardedHeaders:KnownProxies</c> or
/// <c>KnownNetworks</c> so the client IP is seen (see ForwardedHeadersSetup); an IPv6 client is limited per
/// /64, the block one subscriber usually holds.
/// </summary>
public static class RateLimitingExtensions
{
    public const string CodeRequests = "code-requests";
    public const string Logins = "logins";
    public const string EmployeeAppLogins = "employee-app-logins";
    public const string EmployeeLoginCodes = "employee-login-codes";
    public const string PaymentNotifications = "payment-notifications";
    public const string ProvenancePages = "provenance-pages";

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
            options.AddPolicy(EmployeeAppLogins, context => PerClient(context, o => o.EmployeeAppLoginsPerMinute));
            options.AddPolicy(EmployeeLoginCodes, context => PerAccount(context, o => o.EmployeeLoginCodesPerMinute));
            options.AddPolicy(PaymentNotifications, context => PerClient(context, o => o.PaymentNotificationsPerMinute));
            options.AddPolicy(ProvenancePages, context => PerClient(context, o => o.ProvenancePagesPerMinute));
        });
        return services;
    }

    private static RateLimitPartition<string> PerClient(HttpContext context, Func<RateLimitOptions, int> permits)
    {
        var limit = permits(context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    /// <summary>
    /// One bucket per signed-in account (the <c>sub</c> of a token the authentication middleware accepted, which therefore runs before
    /// the limiter), so a forged token cannot burn another account's budget. A caller without an accepted token is limited per client IP
    /// under a separate key and gets 401 anyway.
    /// </summary>
    private static RateLimitPartition<string> PerAccount(HttpContext context, Func<RateLimitOptions, int> permits)
    {
        var account = context.User.FindFirstValue(OmtsClaimTypes.Subject);
        if (string.IsNullOrEmpty(account))
        {
            return PerClient(context, permits);
        }

        var limit = permits(context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter(
            $"account:{account}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}
