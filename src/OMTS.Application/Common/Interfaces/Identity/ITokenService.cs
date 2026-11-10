using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>Issues JWT access tokens and opaque refresh tokens for login sessions.</summary>
public interface ITokenService
{
    /// <summary>
    /// Creates a short-lived access token bound to <paramref name="sessionId"/>. It expires at the configured lifetime, or at
    /// <paramref name="sessionExpiresAt"/> (the session's absolute expiry) when that comes first; the caller must pass a session
    /// that is still usable.
    /// </summary>
    AccessToken CreateAccessToken(Account account, Guid sessionId, DateTimeOffset sessionExpiresAt);

    /// <summary>
    /// Creates a random refresh token for a session; only its <see cref="HashRefreshToken"/> is stored.
    /// The token names its session, so presenting any older token of the session is recognised as reuse.
    /// </summary>
    string CreateRefreshToken(Guid sessionId);

    /// <summary>Session a refresh token was issued for, or null when the token is malformed.</summary>
    Guid? ReadSessionId(string refreshToken);

    string HashRefreshToken(string refreshToken);

    /// <summary>
    /// Keyed hash of the client's random device id (see <c>DeviceIds</c>), so a database leak does not reveal the ids that
    /// browsers and apps keep. The same id always gives the same key; the id itself is never stored.
    /// </summary>
    string DeriveDeviceKey(string deviceId);

    SessionLifetime GetSessionLifetime(ClientType clientType);
}
