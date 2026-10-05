using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Omts.Api.Hosting;

public static class PlatformServices
{
    public static IServiceCollection AddPlatform(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<HostingOptions>()
            .Bind(configuration.GetSection(HostingOptions.SectionName))
            .Validate(options => options.MaxRequestBodyBytes is > 0 and <= 104_857_600,
                "Hosting:MaxRequestBodyBytes must be between 1 and 104857600.")
            .Validate(options => options.RequestTimeoutSeconds is > 0 and <= 300,
                "Hosting:RequestTimeoutSeconds must be between 1 and 300.")
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HostFilteringOptions>, AllowedHostsValidator>();
        services.AddOptions<HostFilteringOptions>()
            .Configure(options => options.IncludeFailureMessage = false)
            .ValidateOnStart();
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<HostingOptions>>((options, hosting) =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = hosting.Value.MaxRequestBodyBytes;
            });
        services.AddRequestTimeouts();
        services.AddOptions<RequestTimeoutOptions>()
            .Configure<IOptions<HostingOptions>>((options, hosting) =>
                options.DefaultPolicy = new RequestTimeoutPolicy
                {
                    Timeout = TimeSpan.FromSeconds(hosting.Value.RequestTimeoutSeconds),
                    TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
                    WriteTimeoutResponse = context => PlatformProblems.WriteAsync(context, StatusCodes.Status504GatewayTimeout)
                });
        services.AddProblemDetails(options => options.CustomizeProblemDetails = PlatformProblems.Customize);
        // The writer also serves a generic problem when a client's Accept header excludes JSON.
        services.AddSingleton<IProblemDetailsWriter, PlatformProblemWriter>();
        services.AddHealthChecks().AddCheck<ApplicationReadinessHealthCheck>("application", tags: ["ready"]);

        bool exportTelemetry = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(environment.ApplicationName))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (exportTelemetry)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (exportTelemetry)
                {
                    metrics.AddOtlpExporter();
                }
            });
        return services;
    }
}
