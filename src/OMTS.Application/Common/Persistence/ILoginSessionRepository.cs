using OMTS.Domain.Entities;

namespace OMTS.Application.Common.Persistence;

public interface ILoginSessionRepository
{
    Task<LoginSession?> GetAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>Session whose current refresh token has this hash, with its account loaded.</summary>
    Task<LoginSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    /// <summary>Session whose previous (already rotated) refresh token has this hash.</summary>
    Task<LoginSession?> GetByPreviousRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    /// <summary>Not revoked, within its absolute lifetime, and the account is still Active (NFR-01).</summary>
    Task<bool> IsActiveAsync(Guid accountId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(LoginSession session);
}
