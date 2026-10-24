namespace OMTS.Api.Extensions;

/// <summary>
/// "Cors:AllowedOrigins": the browser origins (e.g. the Next.js web app) allowed to call the API. Empty, the
/// default, means no CORS policy at all, so browsers keep refusing cross-origin calls. Tokens travel in headers
/// and bodies, never in cookies, so credentials are not allowed.
/// </summary>
public static class CorsSetup
{
    public const string AllowedOriginsKey = "Cors:AllowedOrigins";
    private const string PolicyName = "WebApp";

    public static IServiceCollection AddOmtsCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = AllowedOrigins(configuration);
        if (origins.Length > 0)
        {
            services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                // Spreadsheet exports name their file in this header.
                .WithExposedHeaders("Content-Disposition")));
        }

        return services;
    }

    public static void UseOmtsCors(this WebApplication app)
    {
        if (AllowedOrigins(app.Configuration).Length > 0)
        {
            app.UseCors(PolicyName);
        }
    }

    private static string[] AllowedOrigins(IConfiguration configuration) =>
        (configuration.GetSection(AllowedOriginsKey).Get<string[]>() ?? [])
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(origin => origin.Trim().TrimEnd('/'))
            .ToArray();
}
