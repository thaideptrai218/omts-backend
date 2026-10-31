using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories.Purchasing;

internal sealed class SupplierRepository(OmtsDbContext db) : ISupplierRepository
{
    public Task<Supplier?> GetForEditAsync(Guid supplierId, CancellationToken cancellationToken) =>
        db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

    public Task<Supplier?> FindByPhoneAsync(string phoneNumber, Guid? exceptSupplierId, CancellationToken cancellationToken) =>
        db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.PhoneNumber == phoneNumber && s.Id != exceptSupplierId, cancellationToken);

    public async Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
        string? search, SupplierType? type, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(s => s.Name.Contains(text) || s.PhoneNumber.Contains(text));
        }

        if (type is not null)
        {
            query = query.Where(s => s.SupplierType == type);
        }

        if (statusId is not null)
        {
            query = query.Where(s => s.StatusId == statusId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(s => s.Name).ThenBy(s => s.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add(Supplier supplier) => db.Suppliers.Add(supplier);
}
