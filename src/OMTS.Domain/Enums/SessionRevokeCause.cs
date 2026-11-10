namespace OMTS.Domain.Enums;

/// <summary>Why a login session was ended, as far as a client has to tell the difference.</summary>
public enum SessionRevokeCause
{
    /// <summary>
    /// A person or the system ended it on purpose: logout, a session or device revoked, an account lock, a password change or
    /// reset, a removed Zalo link. A client must not sign itself in again silently.
    /// </summary>
    Revoked,

    /// <summary>An already replaced refresh token was presented again, so the session was ended as possibly stolen.</summary>
    TokenReuse,
}
