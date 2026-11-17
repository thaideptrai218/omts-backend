using System.Runtime.CompilerServices;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>The host each test client talks to, so that a helper given only the client can reach that host's database.</summary>
public static class TestHosts
{
    private static readonly ConditionalWeakTable<HttpClient, IServiceProvider> Hosts = new();

    /// <summary>Called for every client a test factory makes.</summary>
    public static void Register(HttpClient client, IServiceProvider services) => Hosts.AddOrUpdate(client, services);

    public static IServiceProvider ServicesOf(HttpClient client) =>
        Hosts.TryGetValue(client, out var services)
            ? services
            : throw new InvalidOperationException("This client was not made by a test factory: pass what the helper needs from the factory instead.");
}
