using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories.Sales;

internal sealed class PortWeighingRepository(OmtsDbContext db) : IPortWeighingRepository
{
    public void Add(PortWeighing weighing) => db.PortWeighings.Add(weighing);

    public void AddCage(PortWeighingCage cage) => db.PortWeighingCages.Add(cage);

    public void RemoveCages(IReadOnlyCollection<PortWeighingCage> cages) => db.PortWeighingCages.RemoveRange(cages);

    public Task<PortWeighing?> GetForEditAsync(Guid weighingId, CancellationToken cancellationToken) =>
        WithDetails(db.PortWeighings).FirstOrDefaultAsync(w => w.Id == weighingId, cancellationToken);

    public Task<PortWeighing?> GetAsync(Guid weighingId, CancellationToken cancellationToken) =>
        WithDetails(db.PortWeighings.AsNoTracking()).FirstOrDefaultAsync(w => w.Id == weighingId, cancellationToken);

    public async Task<IReadOnlyList<PortWeighing>> ListOfRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        await WithDetails(db.PortWeighings.AsNoTracking())
            .Where(w => w.PurchaseRequestId == requestId)
            .OrderByDescending(w => w.WeighedAt)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<PortWeighing> Items, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var ofCustomer = db.PortWeighings.AsNoTracking().Where(w => w.PurchaseRequest.CustomerId == customerId);
        var total = await ofCustomer.CountAsync(cancellationToken);
        var items = await WithDetails(ofCustomer)
            .OrderByDescending(w => w.WeighedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<bool> AnyOfRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.PortWeighings.AnyAsync(w => w.PurchaseRequestId == requestId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CagesByLotOfRequestAsync(Guid requestId, Guid? exceptWeighingId, CancellationToken cancellationToken) =>
        await db.PortWeighingCages.AsNoTracking()
            .Where(c => c.PortWeighing.PurchaseRequestId == requestId && c.PortWeighingId != exceptWeighingId)
            .GroupBy(c => c.HarvestLotId)
            .Select(g => new { Lot = g.Key, Cages = g.Count() })
            .ToDictionaryAsync(g => g.Lot, g => g.Cages, cancellationToken);

    public Task<bool> HasUnconfirmedAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.PortWeighings.AnyAsync(w => w.PurchaseRequestId == requestId && w.ConfirmedAt == null, cancellationToken);

    private static IQueryable<PortWeighing> WithDetails(IQueryable<PortWeighing> weighings) =>
        weighings.AsSplitQuery()
            .Include(w => w.PurchaseRequest).ThenInclude(r => r.Customer)
            .Include(w => w.Cages).ThenInclude(c => c.HarvestLot).ThenInclude(l => l.Raft)
            .Include(w => w.Invoices.Where(i => i.StatusId == StatusIds.SalesInvoice.Completed));
}
