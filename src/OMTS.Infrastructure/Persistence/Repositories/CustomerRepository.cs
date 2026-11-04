using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CustomerRepository(OmtsDbContext db) : ICustomerRepository
{
    public async Task<IReadOnlyList<Customer>> GetOfflineByPhoneAsync(string phoneNumber, CancellationToken cancellationToken) =>
        await db.Customers
            .Where(c => c.PhoneNumber == phoneNumber && c.AccountId == null && c.MergedIntoCustomerId == null)
            .ToListAsync(cancellationToken);

    public Task<Customer?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Customers.FirstOrDefaultAsync(c => c.AccountId == accountId, cancellationToken);

    public Task<Customer?> GetForEditAsync(Guid customerId, CancellationToken cancellationToken) =>
        db.Customers.Include(c => c.Account).FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);

    public Task<Customer?> FindByPhoneAsync(string phoneNumber, Guid? exceptCustomerId, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.PhoneNumber == phoneNumber && c.MergedIntoCustomerId == null && c.Id != exceptCustomerId, cancellationToken);

    public async Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(string? search, string? phoneNumber, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Customers.AsNoTracking().Where(c => c.MergedIntoCustomerId == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = phoneNumber is null
                ? query.Where(c => c.Name.Contains(text) || (c.PhoneNumber != null && c.PhoneNumber.Contains(text)))
                : query.Where(c => c.Name.Contains(text) || (c.PhoneNumber != null && (c.PhoneNumber.Contains(text) || c.PhoneNumber == phoneNumber)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(c => c.Account)
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add(Customer customer) => db.Customers.Add(customer);
}
