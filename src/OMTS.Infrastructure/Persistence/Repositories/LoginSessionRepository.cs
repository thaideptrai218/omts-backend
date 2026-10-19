using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Persistence;
using OMTS.Domain.Entities;
using OMTS.Domain.Statuses;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class LoginSessionRepository(OmtsDbContext db) : ILoginSessionRepository
{
    public Task<LoginSession?> GetAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken) =>
        db.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.AccountId == accountId, cancellationToken);

    public Task<LoginSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken) =>
        db.LoginSessions
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash, cancellationToken);

    public Task<LoginSession?> GetByPreviousRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken) =>
        db.LoginSessions.FirstOrDefaultAsync(s => s.PreviousRefreshTokenHash == refreshTokenHash, cancellationToken);

    public Task<bool> IsActiveAsync(Guid accountId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.LoginSessions.AnyAsync(
            s => s.Id == sessionId
                && s.AccountId == accountId
                && s.RevokedAt == null
                && s.AbsoluteExpiresAt > now
                && s.Account.StatusId == StatusIds.Account.Active,
            cancellationToken);

    public void Add(LoginSession session) => db.LoginSessions.Add(session);
}
