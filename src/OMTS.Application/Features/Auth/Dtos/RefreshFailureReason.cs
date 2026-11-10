namespace OMTS.Application.Features.Auth.Dtos;

/// <summary>Why a refresh was refused, sent as the problem extension <c>reason</c> so a client knows whether it may sign in again by itself.</summary>
public enum RefreshFailureReason
{
    /// <summary>The session's lifetime ended, or it no longer exists: a new login is the normal next step.</summary>
    Expired,

    /// <summary>A refresh token that was already replaced was presented again (a lost or stolen response); the session was ended.</summary>
    Reuse,

    /// <summary>A user or an administrator ended the session: logout elsewhere, a session or device revoked, an account lock, a password change.</summary>
    Revoked,
}
