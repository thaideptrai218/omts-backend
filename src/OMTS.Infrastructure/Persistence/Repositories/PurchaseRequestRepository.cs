using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class PurchaseRequestRepository(OmtsDbContext db) : IPurchaseRequestRepository
{
    public Task<PurchaseRequest?> GetAsync(Guid id, CancellationToken ct) =>
        db.Set<PurchaseRequest>().Include(r => r.Customer).FirstOrDefaultAsync(r => r.Id == id, ct);
    public Task<PurchaseRequest?> FindOperationAsync(Guid customerId, Guid operationId, CancellationToken ct) =>
        db.Set<PurchaseRequest>().Include(r => r.Customer).FirstOrDefaultAsync(r => r.CustomerId == customerId && r.ClientOperationId == operationId, ct);
    public async Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(Guid? customerId, int? statusId, int page, int pageSize, CancellationToken ct)
    {
        var q = db.Set<PurchaseRequest>().AsNoTracking().Include(r => r.Customer).AsQueryable();
        if (customerId is { } c) q = q.Where(r => r.CustomerId == c);
        if (statusId is { } s) q = q.Where(r => r.StatusId == s);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }
    public void Add(PurchaseRequest request) => db.Set<PurchaseRequest>().Add(request);
}
