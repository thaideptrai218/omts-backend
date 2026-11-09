using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ILoginSessionRepository
{
    Task<LoginSession?> GetAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>Session by id alone, used to revoke it when one of its old refresh tokens is replayed.</summary>
    Task<LoginSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Session whose current refresh token has this hash, with its account loaded.</summary>
    Task<LoginSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    /// <summary>Session whose previous (just replaced) refresh token has this hash, with its account loaded.</summary>
    Task<LoginSession?> GetByPreviousRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    /// <summary>Not revoked, within its absolute lifetime, and the account is still Active (NFR-01).</summary>
    Task<bool> IsActiveAsync(Guid accountId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revokes every open session of the account (password change/reset, lock); saved with the caller's change.</summary>
    Task RevokeAllAsync(Guid accountId, DateTimeOffset now, string reason, CancellationToken cancellationToken);

    /// <summary>Revokes every open session of the account that was opened with <paramref name="method"/>; saved with the caller's change.</summary>
    Task RevokeByMethodAsync(Guid accountId, AuthMethod method, DateTimeOffset now, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Sessions of the account that are not revoked and still inside their absolute lifetime, newest first, tracked so the caller
    /// can revoke them in its own save (every revocation must go through a tracked change to reach the revocation store).
    /// </summary>
    Task<IReadOnlyList<LoginSession>> ListUsableAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The newest session of the account that was opened with this device key, open or ended; null when there is none.</summary>
    Task<LoginSession?> GetLatestForDeviceAsync(Guid accountId, string deviceKey, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes sessions whose refresh lifetime ended, or that were revoked, before <paramref name="cutoff"/>, and detaches the push
    /// devices that still point to them. Runs in batches and saves by itself; returns the number of sessions deleted.
    /// </summary>
    Task<int> DeleteEndedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    void Add(LoginSession session);
}
