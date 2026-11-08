using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class PendingSaleRepository(OmtsDbContext db) : IPendingSaleRepository
{
    public void Add(PendingSale sale) => db.PendingSales.Add(sale);

    public Task<PendingSale?> GetForEditAsync(string code, CancellationToken cancellationToken) =>
        db.PendingSales.Include(s => s.Zone).Include(s => s.Customer).FirstOrDefaultAsync(s => s.Code == code, cancellationToken);

    public Task<PendingSale?> GetAsync(string code, CancellationToken cancellationToken) =>
        db.PendingSales.AsNoTracking().Include(s => s.Zone).Include(s => s.Customer).FirstOrDefaultAsync(s => s.Code == code, cancellationToken);

    public Task<string?> FindCodeByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        db.PendingSales.AsNoTracking().Where(s => s.DeduplicationKey == key).Select(s => s.Code).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PendingSale>> ListAwaitingAsync(IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken)
    {
        var awaiting = db.PendingSales.AsNoTracking().Include(s => s.Zone).Include(s => s.Customer).Where(s => s.Status == PendingSaleStatus.Awaiting);
        if (zoneIds is not null)
        {
            var ids = zoneIds.ToList();
            awaiting = awaiting.Where(s => ids.Contains(s.ZoneId));
        }

        return await awaiting.OrderBy(s => s.CreatedAt).ThenBy(s => s.Code).ToListAsync(cancellationToken);
    }
}
