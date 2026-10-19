using OMTS.Domain.Enums;

namespace OMTS.Application.Authentication;

/// <param name="Identifier">Username, verified phone number or verified email (UC02).</param>
public sealed record LoginRequest(string Identifier, string Password, ClientType ClientType = ClientType.Web, string? DeviceName = null);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>Request metadata stored on the session for the user's device list.</summary>
public sealed record ClientInfo(string? UserAgent, string? IpAddress);

public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid AccountId,
    string DisplayName,
    SubjectType SubjectType,
    bool MustChangePassword,
    bool PhoneVerificationRequired);

/// <summary>Why authentication failed; the SRS message code is what clients display.</summary>
public enum AuthError
{
    /// <summary>MSG12 – generic, never says whether the identifier or the password is wrong.</summary>
    InvalidCredentials,

    /// <summary>MSG69 – 5 failed logins, locked for 15 minutes.</summary>
    TemporarilyLocked,

    /// <summary>MSG13 – account Locked, or staff without an effective zone–role pair.</summary>
    AccessDenied,

    /// <summary>MSG68 – temporary password not changed within 24 hours.</summary>
    TemporaryPasswordExpired,

    /// <summary>MSG19 – refresh token unknown, expired or revoked; log in again.</summary>
    SessionExpired,
}

public sealed record AuthResult(AuthTokens? Tokens, AuthError? Error)
{
    public static AuthResult Success(AuthTokens tokens) => new(tokens, null);
    public static AuthResult Failure(AuthError error) => new(null, error);
}
