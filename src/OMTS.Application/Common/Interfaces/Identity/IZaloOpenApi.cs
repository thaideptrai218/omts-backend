using OMTS.Application.Common.Models;

namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>
/// Zalo Open API adapter for "Liên kết tài khoản" on the Zalo Mini App. Only the server calls it: the phone
/// token is exchanged with the Zalo App secret, which never leaves the server. Provider errors and timeouts
/// are returned as <see cref="ZaloLookupOutcome.Unavailable"/>, never thrown.
/// </summary>
public interface IZaloOpenApi
{
    /// <summary>Exchange the single-use getLocation token using the server-side App secret.</summary>
    Task<ZaloLocationLookup> GetLocationAsync(string accessToken, string locationToken, CancellationToken cancellationToken);

    /// <summary>Zalo user id behind an access token from <c>getAccessToken()</c> of the Mini App.</summary>
    Task<ZaloLookup> GetUserIdAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>
    /// Phone number behind a token from <c>getPhoneNumber()</c> of the Mini App, as Zalo writes it (e.g.
    /// "84912345678"). A phone token works once and for about two minutes.
    /// </summary>
    Task<ZaloLookup> GetPhoneNumberAsync(string accessToken, string phoneToken, CancellationToken cancellationToken);
}
