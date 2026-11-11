using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CustomerDebtAdjustmentRepository(OmtsDbContext db) : ICustomerDebtAdjustmentRepository
{
    public void Add(CustomerDebtAdjustment adjustment) => db.CustomerDebtAdjustments.Add(adjustment);

    public Task<CustomerDebtAdjustment?> GetForEditAsync(Guid adjustmentId, CancellationToken cancellationToken) =>
        db.CustomerDebtAdjustments.Include(a => a.Customer).FirstOrDefaultAsync(a => a.Id == adjustmentId, cancellationToken);

    public Task<CustomerDebtAdjustment?> GetAsync(Guid adjustmentId, CancellationToken cancellationToken) =>
        db.CustomerDebtAdjustments.AsNoTracking().Include(a => a.Customer).FirstOrDefaultAsync(a => a.Id == adjustmentId, cancellationToken);
}
