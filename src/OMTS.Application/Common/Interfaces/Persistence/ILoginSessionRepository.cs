using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ILoginSessionRepository
{
    Task<LoginSession?> GetAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>Session by id alone, used to revoke it when one of its old refresh tokens is replayed.</summary>
    Task<LoginSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Session whose current refresh token has this hash, with its account loaded.</summary>
    Task<LoginSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    /// <summary>Not revoked, within its absolute lifetime, and the account is still Active (NFR-01).</summary>
    Task<bool> IsActiveAsync(Guid accountId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revokes every open session of the account (password change/reset, lock); saved with the caller's change.</summary>
    Task RevokeAllAsync(Guid accountId, DateTimeOffset now, string reason, CancellationToken cancellationToken);

    void Add(LoginSession session);
}
