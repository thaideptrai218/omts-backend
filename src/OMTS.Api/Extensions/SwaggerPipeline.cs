using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace OMTS.Api.Extensions;

/// <summary>
/// Where Swagger is served. Development: open, as before. Elsewhere (council decision 4, OMTS-216: closed on a shared
/// server) it stays off (404) unless <c>Swagger:Enabled</c> is true, and then every <c>/swagger</c> address first asks
/// for HTTP Basic credentials taken from configuration (<c>Swagger:Username</c>, <c>Swagger:Password</c> of at least
/// 16 characters). Turned on without them, the host refuses to start. The API endpoints are unchanged: they still need
/// their bearer token, which the Swagger page's Authorize button sends.
/// </summary>
public static class SwaggerPipeline
{
    public const string EnabledKey = "Swagger:Enabled";
    public const string UsernameKey = "Swagger:Username";
    public const string PasswordKey = "Swagger:Password";
    public const int MinimumPasswordLength = 16;

    private const string Challenge = "Basic realm=\"OMTS API docs\", charset=\"UTF-8\"";

    public static WebApplication UseOmtsSwagger(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
            return app;
        }

        // Blank (an unset variable passed through) is off; anything else must be a boolean.
        if (string.IsNullOrWhiteSpace(app.Configuration[EnabledKey]) || !app.Configuration.GetValue<bool>(EnabledKey))
        {
            return app;
        }

        var username = app.Configuration[UsernameKey];
        var password = app.Configuration[PasswordKey];
        if (string.IsNullOrWhiteSpace(username) || username.Contains(':') || password is null || password.Length < MinimumPasswordLength)
        {
            // Never repeats the configured values.
            throw new InvalidOperationException(
                $"{EnabledKey}=true outside Development needs {UsernameKey} (no ':') and {PasswordKey} " +
                $"({MinimumPasswordLength}+ characters); without them Swagger would be open to anyone with the URL.");
        }

        // Compared as SHA-256 digests in constant time: neither the content nor the length of the secret leaks.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes($"{username}:{password}"));
        // Swashbuckle runs only inside the checked branch: its own path matching is looser than a segment
        // (its UI also answers "/swaggerindex.html"), so nothing it could serve is reachable around the login.
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase),
            branch =>
            {
                branch.Use(async (context, next) =>
                {
                    // Never kept by a cache in front (CloudFront): neither the login prompt nor the documents.
                    context.Response.Headers.CacheControl = "no-store";
                    if (HasCredentials(context.Request, expected))
                    {
                        await next(context);
                        return;
                    }

                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = Challenge;
                });
                branch.UseSwagger();
                branch.UseSwaggerUI();
            });
        return app;
    }

    private static bool HasCredentials(HttpRequest request, byte[] expected)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter))
        {
            return false;
        }

        byte[] given;
        try
        {
            given = Convert.FromBase64String(header.Parameter);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(given), expected);
    }
}
