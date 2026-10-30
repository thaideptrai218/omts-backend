using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace OMTS.Api.Extensions;

/// <summary>
/// Behind a reverse proxy every request comes from the proxy's address, so the per-IP rate limits would put
/// all users in one bucket. The client address from X-Forwarded-For is used only when the request comes
/// from a configured proxy (or loopback), so a client cannot pick its own bucket by sending the header.
/// </summary>
public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddOmtsForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = configuration.GetSection(TrustedProxyOptions.SectionName).Get<TrustedProxyOptions>() ?? new TrustedProxyOptions();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in proxies.KnownProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in proxies.KnownNetworks)
            {
                var parts = network.Split('/');
                options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
            }
        });
        return services;
    }
}
