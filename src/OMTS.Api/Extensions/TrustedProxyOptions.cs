using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace OMTS.Api.Extensions;

/// <summary>"ForwardedHeaders" section: the reverse proxies whose X-Forwarded-For / -Proto are trusted.</summary>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Proxy addresses, e.g. "10.0.0.4".</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>Proxy networks in CIDR form, e.g. "10.0.0.0/24".</summary>
    public List<string> KnownNetworks { get; set; } = [];
}
