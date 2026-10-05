using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Omts.Api.Hosting;

namespace Omts.Api.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("*.example.com")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    [InlineData("https://example.com")]
    [InlineData("example.com:8080")]
    public async Task ProductionRequiresExplicitAllowedHosts(string hosts)
    {
        await using WebApplication app = CreateStartupHost(new Dictionary<string, string?> { ["AllowedHosts"] = hosts });
        OptionsValidationException exception = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        Assert.Contains("AllowedHosts", exception.Message);
        Assert.False(app.Lifetime.ApplicationStarted.IsCancellationRequested);
    }

    [Theory]
    [InlineData("localhost;example.com;127.0.0.1;[::1]")]
    [InlineData("example.com;localhost")]
    public async Task ExplicitHostListsAllowStartupAndListedHostRequests(string hosts)
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["AllowedHosts"] = hosts });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Hosting:MaxRequestBodyBytes", "0")]
    [InlineData("Hosting:MaxRequestBodyBytes", "104857601")]
    [InlineData("Hosting:RequestTimeoutSeconds", "0")]
    [InlineData("Hosting:RequestTimeoutSeconds", "301")]
    public async Task InvalidHostingLimitsFailStartup(string key, string value)
    {
        await using WebApplication app = CreateStartupHost(new Dictionary<string, string?> { [key] = value });
        OptionsValidationException exception = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        Assert.Contains(key, exception.Message);
        Assert.False(app.Lifetime.ApplicationStarted.IsCancellationRequested);
    }

    [Fact]
    public async Task KestrelUsesValidatedBodyLimitAndOmitsServerHeader()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["Hosting:MaxRequestBodyBytes"] = "4096"
        });
        using var client = factory.CreateClient();
        KestrelServerOptions options = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Equal(4096, options.Limits.MaxRequestBodySize);
        Assert.False(options.AddServerHeader);
    }

    [Fact]
    public async Task ReadinessIsUnavailableBeforeStartupAndWhileStopping()
    {
        using var lifetime = new TestLifetime();
        var check = new ApplicationReadinessHealthCheck(lifetime);
        var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext();
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            (await check.CheckHealthAsync(context)).Status);
        lifetime.Started.Cancel();
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            (await check.CheckHealthAsync(context)).Status);
        lifetime.StopApplication();
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            (await check.CheckHealthAsync(context)).Status);
    }

    private static WebApplication CreateStartupHost(Dictionary<string, string?> configuration)
    {
        // Own startup and disposal directly so a failed entry point cannot dispose the host before its error is observed.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            EnvironmentName = Environments.Production
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "localhost"
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.WebHost.UseTestServer();
        builder.Services.AddPlatform(builder.Configuration, builder.Environment);
        return builder.Build();
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        public CancellationTokenSource Started { get; } = new();
        private CancellationTokenSource Stopping { get; } = new();
        private CancellationTokenSource Stopped { get; } = new();
        public CancellationToken ApplicationStarted => Started.Token;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public CancellationToken ApplicationStopped => Stopped.Token;
        public void StopApplication() => Stopping.Cancel();
        public void Dispose()
        {
            Started.Dispose();
            Stopping.Dispose();
            Stopped.Dispose();
        }
    }
}
