using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Authentication;
using OMTS.Application.Common.Interfaces;
using OMTS.Infrastructure.Authentication;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "OmtsDb";

    /// <summary>Registers persistence and external adapters.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<PermissionCacheInvalidationInterceptor>();
        services.AddDbContext<OmtsDbContext>((provider, options) =>
        {
            options.AddInterceptors(provider.GetRequiredService<PermissionCacheInvalidationInterceptor>());

            // Resolved lazily so the API can start (e.g. /health, tests) before a database is configured.
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not set. " +
                    $"Run: dotnet user-secrets set \"ConnectionStrings:{ConnectionStringName}\" \"<value>\" --project src/OMTS.Api");
            }

            options.UseSqlServer(connectionString);
        });
        services.AddScoped<IOmtsDbContext>(provider => provider.GetRequiredService<OmtsDbContext>());

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        return services;
    }
}
