using System.Net;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="AuthApiFactory"/> that also serves the API on a real Kestrel socket, for behaviour TestServer
/// does not have (the request body size limit). Both hosts share the in-memory database, so the helpers of
/// <see cref="AuthApiFactory"/> seed data the Kestrel host sees.
/// </summary>
public sealed class KestrelApiFactory : AuthApiFactory
{
    private IHost? _kestrelHost;

    /// <summary>
    /// A client of the Kestrel host (not of TestServer). It waits up to 30 s for "100 Continue" (HttpClient's
    /// default is 1 s), so under load a refused body is still never sent.
    /// </summary>
    public HttpClient CreateKestrelClient()
    {
        // Builds both hosts on first use.
        _ = Services;
        var address = _kestrelHost!.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(30) }) { BaseAddress = new Uri(address) };
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // TestServer host first, then the same builder again on Kestrel with a free loopback port.
        var testHost = builder.Build();
        builder.ConfigureWebHost(web => web.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)));
        _kestrelHost = builder.Build();
        _kestrelHost.Start();
        testHost.Start();
        return testHost;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _kestrelHost?.StopAsync().GetAwaiter().GetResult();
            _kestrelHost?.Dispose();
        }

        base.Dispose(disposing);
    }
}
