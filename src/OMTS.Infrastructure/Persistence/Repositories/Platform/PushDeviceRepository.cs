using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories.Platform;

internal sealed class PushDeviceRepository(OmtsDbContext db) : IPushDeviceRepository
{
    public Task<PushDevice?> GetByTokenAsync(string token, CancellationToken cancellationToken) =>
        db.PushDevices.FirstOrDefaultAsync(d => d.Token == token, cancellationToken);

    public async Task<IReadOnlyList<PushDevice>> ListDeliverableAsync(
        IReadOnlyCollection<Guid> accountIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.PushDevices
            .AsNoTracking()
            .Where(d => accountIds.Contains(d.AccountId)
                && d.PermissionGranted
                && d.LoginSession != null
                && d.LoginSession.AccountId == d.AccountId
                && d.LoginSession.RevokedAt == null
                && d.LoginSession.RefreshTokenExpiresAt > now)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PushDevice>> ListForSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.PushDevices.Where(d => d.LoginSessionId == sessionId).ToListAsync(cancellationToken);

    public void Add(PushDevice device) => db.PushDevices.Add(device);

    public void Remove(PushDevice device) => db.PushDevices.Remove(device);
}
