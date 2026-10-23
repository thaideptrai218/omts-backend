using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>Issues JWT access tokens and opaque refresh tokens for login sessions.</summary>
public interface ITokenService
{
    /// <summary>Creates a short-lived access token bound to <paramref name="sessionId"/>.</summary>
    AccessToken CreateAccessToken(Account account, Guid sessionId);

    /// <summary>
    /// Creates a random refresh token for a session; only its <see cref="HashRefreshToken"/> is stored.
    /// The token names its session, so presenting any older token of the session is recognised as reuse.
    /// </summary>
    string CreateRefreshToken(Guid sessionId);

    /// <summary>Session a refresh token was issued for, or null when the token is malformed.</summary>
    Guid? ReadSessionId(string refreshToken);

    string HashRefreshToken(string refreshToken);

    SessionLifetime GetSessionLifetime(ClientType clientType);
}
