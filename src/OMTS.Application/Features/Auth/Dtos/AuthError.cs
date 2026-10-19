namespace OMTS.Application.Features.Auth.Dtos;

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
