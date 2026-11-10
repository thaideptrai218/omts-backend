using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="RefreshTokenRotated">
/// True when <paramref name="RefreshToken"/> is a new token that replaces the one the client sent (login, normal refresh).
/// False when the client sent the token its last refresh just replaced (a lost or racing response): the same token comes
/// back with the session's current expiry, the client keeps the token it already holds, and only the access token is new.
/// </param>
public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid AccountId,
    string DisplayName,
    SubjectType SubjectType,
    bool MustChangePassword,
    bool PhoneVerificationRequired,
    bool RefreshTokenRotated = true);
