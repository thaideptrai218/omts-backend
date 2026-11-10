using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace OMTS.Api.Extensions;

/// <summary>
/// "BffClient" section: lets the Next.js server (the BFF) tell the API who the end user is. Browsers reach the API only
/// through the BFF, so without this every request carries the BFF's address and the per-IP limits put all users in one bucket.
/// </summary>
public sealed class BffClientOptions
{
    public const string SectionName = "BffClient";

    /// <summary>Outside Development a configured secret is at least this long.</summary>
    public const int MinimumSecretLength = 32;

    /// <summary>Off by default: the API then ignores the client headers whatever they carry.</summary>
    public bool Enabled { get; set; }

    /// <summary>Shared with the BFF (a secret: user-secrets or an environment variable, never appsettings.json).</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>Development may leave the secret empty; the feature then stays inactive, no header can match an empty secret.</summary>
    public bool IsValid(bool development) => !Enabled || development || SharedSecret.Length >= MinimumSecretLength;
}

/// <summary>
/// A request that proves it comes from the BFF (header <see cref="SecretHeader"/> equals the shared secret) may name the end
/// user's address in <see cref="ClientIpHeader"/> and browser in <see cref="UserAgentHeader"/>. Without a valid secret the
/// two headers are ignored, so a client cannot choose its own rate-limit bucket. An IP allow-list would not do: the BFF
/// may run on a platform whose egress addresses cannot be listed.
/// </summary>
/// <remarks>
/// Two steps around <c>UseForwardedHeaders</c>: the secret is checked and removed before it (nothing later sees it), the
/// values are applied after it, because forwarded headers replace the connection address of a request that comes through a
/// trusted proxy and the BFF's word about the end user is the more specific one.
/// </remarks>
public static class BffClientHeadersSetup
{
    public const string SecretHeader = "X-Bff-Secret";
    public const string ClientIpHeader = "X-Client-Ip";
    public const string UserAgentHeader = "X-Client-User-Agent";

    private const string SuppliedItemKey = "Omts.BffClient";

    public static IServiceCollection AddOmtsBffClientHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BffClientOptions>()
            .Bind(configuration.GetSection(BffClientOptions.SectionName))
            .Validate<IHostEnvironment>(
                (options, environment) => options.IsValid(environment.IsDevelopment()),
                $"{BffClientOptions.SectionName}:SharedSecret must be at least {BffClientOptions.MinimumSecretLength} characters when {BffClientOptions.SectionName}:Enabled is true.")
            .ValidateOnStart();
        return services;
    }

    /// <summary>Before <c>UseForwardedHeaders</c>: checks the secret, keeps what the BFF supplied, and removes the secret from the request.</summary>
    public static IApplicationBuilder UseOmtsBffClientCapture(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var options = context.RequestServices.GetRequiredService<IOptions<BffClientOptions>>().Value;
        var headers = context.Request.Headers;
        if (options.Enabled
            && options.SharedSecret.Length > 0
            && headers.TryGetValue(SecretHeader, out var secret)
            && secret.Count == 1
            && SecretsEqual(secret[0] ?? string.Empty, options.SharedSecret))
        {
            context.Items[SuppliedItemKey] = new Supplied(ReadAddress(headers[ClientIpHeader]), ReadText(headers[UserAgentHeader]));
        }

        headers.Remove(SecretHeader);
        await next(context);
    });

    /// <summary>After <c>UseForwardedHeaders</c>: what the rate limits, logs and the session record see as the client.</summary>
    public static IApplicationBuilder UseOmtsBffClientApply(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Items[SuppliedItemKey] is Supplied supplied)
        {
            if (supplied.Address is not null)
            {
                context.Connection.RemoteIpAddress = supplied.Address;
            }

            if (supplied.UserAgent is not null)
            {
                context.Request.Headers.UserAgent = supplied.UserAgent;
            }
        }

        await next(context);
    });

    // Both sides hashed, so the comparison takes the same time whatever the length of the presented value.
    private static bool SecretsEqual(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    // One header value that is a full IPv4 or IPv6 address: "1", "127.1" or a list are no address (IPAddress.TryParse would take them).
    private static IPAddress? ReadAddress(Microsoft.Extensions.Primitives.StringValues values) =>
        ReadText(values) is { } text
        && (text.Contains(':') || text.Count(c => c == '.') == 3)
        && IPAddress.TryParse(text, out var address)
            ? address
            : null;

    private static string? ReadText(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]) ? values[0]!.Trim() : null;

    private sealed record Supplied(IPAddress? Address, string? UserAgent);
}
