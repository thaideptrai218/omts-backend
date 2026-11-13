using System.Net;

namespace OMTS.Infrastructure.Zalo;

/// <summary>
/// The HTTP handler of the Zalo Open API client. With <c>Zalo:ProxyUrl</c> set, every Zalo call goes through that forward
/// proxy (a tunnel to graph.zalo.me, so TLS still ends at Zalo); only this client uses it. Without it the handler keeps the
/// framework defaults, as before. The URL holds the proxy password, so no message or log line below repeats it.
/// </summary>
internal static class ZaloProxyHandlerFactory
{
    public static SocketsHttpHandler Create(ZaloOptions options)
    {
        var handler = new SocketsHttpHandler();
        if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            handler.Proxy = ParseProxy(options.ProxyUrl);
        }

        return handler;
    }

    private static WebProxy ParseProxy(string value)
    {
        // Port: a socks5 address has no default port, and a proxy without one would only fail at the first call.
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "socks5")
            || url.Host.Length == 0
            || url.Port < 1)
        {
            throw new InvalidOperationException(
                $"{ZaloOptions.SectionName}:ProxyUrl must be an absolute http:// or socks5:// URL with a host and a port, " +
                "e.g. http://<user>:<password>@<host>:<port> (user and password percent-encoded).");
        }

        // The address keeps no credentials; they go to the proxy as the Basic / SOCKS5 login instead.
        var proxy = new WebProxy(new UriBuilder(url.Scheme, url.Host, url.Port).Uri);
        if (url.UserInfo.Length > 0)
        {
            var parts = url.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        return proxy;
    }
}
