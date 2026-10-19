using OMTS.Domain.Entities;
using OMTS.Domain.Enums;

namespace OMTS.Application.Authentication;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Sliding refresh lifetime and hard session limit for a client type (NFR-01).</summary>
public sealed record SessionLifetime(TimeSpan RefreshTokenLifetime, TimeSpan MaxLifetime);

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
