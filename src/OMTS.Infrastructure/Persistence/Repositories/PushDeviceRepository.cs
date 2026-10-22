using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class PushDeviceRepository(OmtsDbContext db) : IPushDeviceRepository
{
    public Task<PushDevice?> GetByTokenAsync(string token, CancellationToken cancellationToken) =>
        db.PushDevices.FirstOrDefaultAsync(d => d.Token == token, cancellationToken);

    public async Task<IReadOnlyList<PushDevice>> ListForAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.PushDevices.Where(d => d.AccountId == accountId).ToListAsync(cancellationToken);

    public void Add(PushDevice device) => db.PushDevices.Add(device);

    public void Remove(PushDevice device) => db.PushDevices.Remove(device);
}
