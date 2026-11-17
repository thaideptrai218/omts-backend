using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Infrastructure;
using OMTS.Infrastructure.Zalo;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>
/// <c>Zalo:ProxyUrl</c> through the real registration (<c>AddZalo</c>): the calls of the Graph adapter leave through the proxy
/// with its login. A local stand-in answers as a proxy would; nothing reaches the internet. No SQL needed.
/// </summary>
public class ZaloProxyRoutingTests
{
    // Generated per run so no password-like literal is committed (gitleaks). The password needs percent-encoding in a URL.
    private readonly string _user = "u" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
    private readonly string _password = "p@ss:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + "/%";

    [Fact]
    public async Task Zalo_calls_leave_through_the_proxy_with_its_login()
    {
        await using var proxy = FakeConnectProxy.Start();
        using var provider = Provider($"http://{_user}:{Uri.EscapeDataString(_password)}@127.0.0.1:{proxy.Port}");

        var lookup = await provider.GetRequiredService<IZaloOpenApi>().GetUserIdAsync("access-1", CancellationToken.None);

        // The stand-in refuses the tunnel, so Zalo is unreachable for the adapter (MSG04) ...
        Assert.Equal(ZaloLookupOutcome.Unavailable, lookup.Outcome);
        // ... but it was asked for graph.zalo.me and got the decoded login of the URL.
        var requests = proxy.Requests.ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal("CONNECT graph.zalo.me:443 HTTP/1.1", request.RequestLine));
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}"));
        Assert.Contains(requests, request => request.ProxyAuthorization == basic);
    }

    [Fact]
    public void A_bad_proxy_url_stops_the_Graph_adapter_without_repeating_the_password()
    {
        using var provider = Provider($"ftp://{_user}:{Uri.EscapeDataString(_password)}@proxy.example.vn:21");

        // The startup check builds the adapter, so the host stops here instead of failing the first user.
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IZaloOpenApi>());

        Assert.Contains("Zalo:ProxyUrl", error.Message);
        Assert.DoesNotContain(Uri.EscapeDataString(_password), error.Message);
        Assert.DoesNotContain(_password, error.Message);
    }

    private static ServiceProvider Provider(string proxyUrl)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zalo:Provider"] = ZaloOptions.GraphProvider,
            ["Zalo:AppSecret"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            ["Zalo:ProxyUrl"] = proxyUrl,
            ["Zalo:TimeoutSeconds"] = "10",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZalo(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>A proxy stand-in: answers 407 until a CONNECT carries a login, then refuses the tunnel. Records what it was asked.</summary>
    private sealed class FakeConnectProxy : IAsyncDisposable
    {
        public sealed record Request(string RequestLine, string? ProxyAuthorization);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task _accepting = Task.CompletedTask;

        public ConcurrentQueue<Request> Requests { get; } = new();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static FakeConnectProxy Start()
        {
            var proxy = new FakeConnectProxy();
            proxy._listener.Start();
            proxy._accepting = proxy.AcceptAsync();
            return proxy;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting;
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    _ = ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token));
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } requestLine)
                    {
                        string? login = null;
                        while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                        {
                            const string name = "Proxy-Authorization:";
                            if (header.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                            {
                                login = header[name.Length..].Trim();
                            }
                        }

                        Requests.Enqueue(new Request(requestLine, login));
                        var answer = login is null
                            ? "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"zalo\"\r\nContent-Length: 0\r\n\r\n"
                            : "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
                        if (login is not null)
                        {
                            return;
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
    }
}
