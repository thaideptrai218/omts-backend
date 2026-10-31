using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories.Identity;

internal sealed class LoginSessionRepository(OmtsDbContext db) : ILoginSessionRepository
{
    public Task<LoginSession?> GetAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken) =>
        db.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.AccountId == accountId, cancellationToken);

    public Task<LoginSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken) =>
        db.LoginSessions
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash, cancellationToken);

    public Task<LoginSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken) =>
        db.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

    public Task<bool> IsActiveAsync(Guid accountId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.LoginSessions.AnyAsync(
            s => s.Id == sessionId
                && s.AccountId == accountId
                && s.RevokedAt == null
                && s.AbsoluteExpiresAt > now
                && s.Account.StatusId == StatusIds.Account.Active,
            cancellationToken);

    public async Task RevokeAllAsync(Guid accountId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var open = await db.LoginSessions.Where(s => s.AccountId == accountId && s.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in open)
        {
            session.Revoke(now, reason);
        }
    }

    public void Add(LoginSession session) => db.LoginSessions.Add(session);
}
