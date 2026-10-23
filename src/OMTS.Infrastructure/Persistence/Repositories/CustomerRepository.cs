using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CustomerRepository(OmtsDbContext db) : ICustomerRepository
{
    public async Task<IReadOnlyList<Customer>> GetOfflineByPhoneAsync(string phoneNumber, CancellationToken cancellationToken) =>
        await db.Customers
            .Where(c => c.PhoneNumber == phoneNumber && c.AccountId == null && c.MergedIntoCustomerId == null
                && c.StatusId == StatusIds.Usage.InUse)
            .ToListAsync(cancellationToken);

    public Task<Customer?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Customers.FirstOrDefaultAsync(c => c.AccountId == accountId, cancellationToken);

    public void Add(Customer customer) => db.Customers.Add(customer);
}
