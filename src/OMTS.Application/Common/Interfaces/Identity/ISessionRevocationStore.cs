namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>
/// A fast, disposable copy of "which sessions were revoked recently". The database stays the source of truth:
/// a store that is missing, empty or broken answers <see cref="SessionRevocationStatus.Unknown"/>, never "valid".
/// Entries only need to outlive the access tokens issued before the revocation.
/// </summary>
public interface ISessionRevocationStore
{
    /// <summary>Never throws; every failure is <see cref="SessionRevocationStatus.Unknown"/>.</summary>
    ValueTask<SessionRevocationStatus> GetStatusAsync(Guid sessionId);

    /// <summary>
    /// Publishes revocations that are already committed. Never throws; false when at least one could not be written,
    /// so the caller can have it repeated.
    /// </summary>
    Task<bool> TryMarkRevokedAsync(IReadOnlyCollection<RevokedSession> sessions);
}
