using System.Net;
using System.Security.Cryptography;
using OMTS.Infrastructure.Zalo;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary><c>Zalo:ProxyUrl</c>: how the URL of the Vietnamese forward proxy becomes the proxy of the Zalo HTTP handler.</summary>
public class ZaloProxyHandlerFactoryTests
{
    // Generated per run so no password-like literal is committed (gitleaks). The password needs percent-encoding in a URL.
    private readonly string _user = "u" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
    private readonly string _password = "p@ss:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + "/%";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_proxy_url_the_handler_keeps_the_framework_defaults(string proxyUrl)
    {
        using var handler = ZaloProxyHandlerFactory.Create(new ZaloOptions { ProxyUrl = proxyUrl });

        Assert.True(handler.UseProxy); // the default: still follows the proxy settings of the host
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void An_http_proxy_has_a_clean_address_and_its_decoded_login_apart()
    {
        using var handler = ZaloProxyHandlerFactory.Create(Options($" http://{_user}:{Uri.EscapeDataString(_password)}@proxy.example.vn:3128 "));

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("http", proxy.Address!.Scheme);
        Assert.Equal("proxy.example.vn", proxy.Address.Host);
        Assert.Equal(3128, proxy.Address.Port);
        Assert.Equal("", proxy.Address.UserInfo);
        var login = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal(_user, login.UserName);
        Assert.Equal(_password, login.Password);
    }

    [Fact]
    public void A_socks5_proxy_without_a_login_has_no_credentials()
    {
        using var handler = ZaloProxyHandlerFactory.Create(Options("socks5://proxy.example.vn:1080"));

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("socks5", proxy.Address!.Scheme);
        Assert.Equal("proxy.example.vn", proxy.Address.Host);
        Assert.Equal(1080, proxy.Address.Port);
        Assert.Null(proxy.Credentials);
    }

    [Fact]
    public void An_ipv6_host_and_an_uppercase_scheme_are_accepted()
    {
        using var handler = ZaloProxyHandlerFactory.Create(Options($"SOCKS5://{_user}:{Uri.EscapeDataString(_password)}@[2001:db8::1]:1080"));

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("socks5", proxy.Address!.Scheme);
        Assert.Equal(1080, proxy.Address.Port);
        Assert.Equal(_password, Assert.IsType<NetworkCredential>(proxy.Credentials).Password);
    }

    // {0} is the login "user:password"; each value is refused at startup and the message never repeats it.
    [Theory]
    [InlineData("ftp://{0}@proxy.example.vn:21")]
    [InlineData("https://{0}@proxy.example.vn:443")]
    [InlineData("socks4://{0}@proxy.example.vn:1080")]
    [InlineData("socks5://{0}@proxy.example.vn")]
    [InlineData("{0}@proxy.example.vn:3128")]
    [InlineData("http://{0}@")]
    [InlineData("not a url {0}")]
    public void A_malformed_proxy_url_is_refused_without_repeating_it(string template)
    {
        var url = string.Format(template, $"{_user}:{Uri.EscapeDataString(_password)}");

        var error = Assert.Throws<InvalidOperationException>(() => ZaloProxyHandlerFactory.Create(Options(url)));

        Assert.Contains("Zalo:ProxyUrl", error.Message);
        Assert.DoesNotContain(_user, error.Message);
        Assert.DoesNotContain(Uri.EscapeDataString(_password), error.Message);
        Assert.DoesNotContain(_password, error.Message);
    }

    private static ZaloOptions Options(string proxyUrl) => new() { ProxyUrl = proxyUrl };
}
