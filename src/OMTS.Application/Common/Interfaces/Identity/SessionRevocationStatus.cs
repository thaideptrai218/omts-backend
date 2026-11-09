namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>What the revocation store knows about a session; <see cref="Unknown"/> sends the caller to the database.</summary>
public enum SessionRevocationStatus
{
    /// <summary>The store cannot answer (unreachable, cold or not configured): ask the database.</summary>
    Unknown,

    /// <summary>The session was revoked; its access tokens must be refused.</summary>
    Revoked,

    /// <summary>The store is complete and does not list the session as revoked.</summary>
    NotRevoked,
}
