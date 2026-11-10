using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

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
        db.LoginSessions
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.PreviousRefreshTokenHash == refreshTokenHash, cancellationToken);

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

    public async Task RevokeByMethodAsync(
        Guid accountId, AuthMethod method, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var open = await db.LoginSessions
            .Where(s => s.AccountId == accountId && s.AuthMethod == method && s.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in open)
        {
            session.Revoke(now, reason);
        }
    }

    public async Task<IReadOnlyList<LoginSession>> ListUsableAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.LoginSessions
            .Where(s => s.AccountId == accountId && s.RevokedAt == null && s.AbsoluteExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<LoginSession?> GetLatestForDeviceAsync(Guid accountId, string deviceKey, CancellationToken cancellationToken) =>
        db.LoginSessions
            .AsNoTracking()
            .Where(s => s.AccountId == accountId && s.DeviceKey == deviceKey)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<int> DeleteEndedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // Batches keep each delete (and its locks) small. The push device rows are released first: a session that is
        // gone cannot deliver anything, and the foreign key would refuse the delete otherwise. Both statements are
        // repeatable, so a failure in between is finished by the next run.
        const int batchSize = 500;
        var deleted = 0;
        while (true)
        {
            var ids = await db.LoginSessions
                .Where(s => s.RefreshTokenExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff))
                .OrderBy(s => s.CreatedAt)
                .Select(s => s.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                return deleted;
            }

            await db.PushDevices
                .Where(d => d.LoginSessionId != null && ids.Contains(d.LoginSessionId.Value))
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.LoginSessionId, (Guid?)null), cancellationToken);
            deleted += await db.LoginSessions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    public void Add(LoginSession session) => db.LoginSessions.Add(session);
}
