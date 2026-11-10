namespace OMTS.Infrastructure.Identity.Revocation;

/// <summary>Redis keys of the revocation store. Identifiers only: no account data is ever stored.</summary>
internal static class RevocationKeys
{
    /// <summary>Present while the sweeper has copied every recent revocation: absence means "ask the database".</summary>
    public const string Ready = "omts:auth:ready";

    public static string Revoked(Guid sessionId) => $"omts:auth:rev:s:{sessionId:D}";
}
