using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>Issues JWT access tokens and opaque refresh tokens for login sessions.</summary>
public interface ITokenService
{
    /// <summary>Creates a short-lived access token bound to <paramref name="sessionId"/>.</summary>
    AccessToken CreateAccessToken(Account account, Guid sessionId);

    /// <summary>Creates a random refresh token; only its <see cref="HashRefreshToken"/> is stored.</summary>
    string CreateRefreshToken();

    string HashRefreshToken(string refreshToken);

    SessionLifetime GetSessionLifetime(ClientType clientType);
}
