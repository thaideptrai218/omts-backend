using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SalesReturnRepository(OmtsDbContext db) : ISalesReturnRepository
{
    public void Add(SalesReturn salesReturn) => db.SalesReturns.Add(salesReturn);

    public Task<SalesReturn?> GetForEditAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.SalesReturns.AsSplitQuery()
            .Include(r => r.Lines).ThenInclude(l => l.HarvestLot).ThenInclude(l => l.Raft)
            .Include(r => r.Invoice).ThenInclude(i => i.Customer)
            .FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);

    public Task<SalesReturn?> GetDetailAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.SalesReturns.AsNoTracking().AsSplitQuery()
            .Include(r => r.Lines).ThenInclude(l => l.HarvestLot)
            .Include(r => r.Invoice).ThenInclude(i => i.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);
}
