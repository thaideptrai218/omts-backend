using System.Globalization;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Zalo;

/// <summary>
/// Zalo Open API stand-in for Development and tests, so the Mini App can be tried outside Zalo: the access token
/// <c>fake:&lt;zalo user id&gt;</c> gives that user id, the phone token <c>fake:&lt;phone number&gt;</c> gives that
/// number. Any other token is refused, like Zalo would.
/// </summary>
public sealed class FakeZaloOpenApi : IZaloOpenApi
{
    public Task<ZaloLocationLookup> GetLocationAsync(string accessToken, string locationToken, CancellationToken cancellationToken)
    {
        var parts = Read(locationToken).Value?.Split(',');
        return Task.FromResult(Read(accessToken).Outcome == ZaloLookupOutcome.Found && parts?.Length == 2
            && decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)
            && lat is >= -90 and <= 90 && lng is >= -180 and <= 180
            ? new ZaloLocationLookup(ZaloLookupOutcome.Found, lat, lng) : ZaloLocationLookup.Rejected);
    }

    public const string Prefix = "fake:";

    public Task<ZaloLookup> GetUserIdAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Read(accessToken));

    public Task<ZaloLookup> GetPhoneNumberAsync(string accessToken, string phoneToken, CancellationToken cancellationToken) =>
        Task.FromResult(Read(accessToken).Outcome == ZaloLookupOutcome.Found ? Read(phoneToken) : ZaloLookup.Rejected);

    private static ZaloLookup Read(string token) =>
        token.StartsWith(Prefix, StringComparison.Ordinal) && token.Length > Prefix.Length
            ? ZaloLookup.Found(token[Prefix.Length..])
            : ZaloLookup.Rejected;
}
