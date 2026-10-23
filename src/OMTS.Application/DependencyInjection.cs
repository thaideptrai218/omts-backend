using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application;

public static class DependencyInjection
{
    /// <summary>Registers application-layer services (use cases, validators).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.AddSingleton(_ => new UnknownLoginCounter());
        services.AddScoped<AuthService>();

        services.AddSingleton<PermissionCache>();
        services.AddSingleton<IPermissionCacheInvalidator>(provider => provider.GetRequiredService<PermissionCache>());
        services.AddScoped<PermissionService>();

        services.AddScoped<AuditLogService>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();

        return services;
    }
}
