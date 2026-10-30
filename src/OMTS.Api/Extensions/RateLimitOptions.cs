using System.Net.Sockets;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Messages;

namespace OMTS.Api.Extensions;

/// <summary>"RateLimits" section: requests per minute and client IP on the anonymous endpoints.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Endpoints that send a code or check one (registration, recovery, resend, verify).</summary>
    public int CodeRequestsPerMinute { get; set; } = 10;

    public int LoginsPerMinute { get; set; } = 30;
}
