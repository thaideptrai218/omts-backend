using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Omts.Api.Tests;

internal sealed class ApiFactory(
    Dictionary<string, string?>? configuration = null,
    Action<IServiceCollection>? configureServices = null,
    string environment = "Production") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, options) => options.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "localhost",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = null
            }));
        if (configuration is not null)
        {
            builder.ConfigureAppConfiguration((_, options) => options.AddInMemoryCollection(configuration));
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, TestBehaviorStartupFilter>();
            configureServices?.Invoke(services);
        });
    }

    // Fault injection belongs to the test host, never to the API's route table.
    private sealed class TestBehaviorStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(async (context, nextMiddleware) =>
            {
                switch (context.Request.Path.Value)
                {
                    case "/__tests/exception":
                        throw new InvalidOperationException("private-database-credentials");
                    case "/__tests/timeout":
                        await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
                        break;
                    default:
                        await nextMiddleware(context);
                        break;
                }
            });
        };
    }
}
