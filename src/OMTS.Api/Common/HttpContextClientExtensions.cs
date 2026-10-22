using OMTS.Application.Features.Auth.Dtos;

namespace OMTS.Api.Common;

internal static class HttpContextClientExtensions
{
    /// <summary>Request metadata stored on a new login session.</summary>
    public static ClientInfo ClientInfo(this HttpContext context) =>
        new(context.Request.Headers.UserAgent.ToString(), context.Connection.RemoteIpAddress?.ToString());
}
