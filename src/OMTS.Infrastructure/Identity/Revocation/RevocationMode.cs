namespace OMTS.Infrastructure.Identity.Revocation;

/// <summary>Where revoked sessions are looked up on each request.</summary>
public enum RevocationMode
{
    /// <summary>Redis when <c>ConnectionStrings:Redis</c> is set, the database otherwise.</summary>
    Auto,

    /// <summary>Redis; the host does not start without its connection string.</summary>
    Redis,

    /// <summary>The database on every request, whatever else is configured.</summary>
    Sql,
}
