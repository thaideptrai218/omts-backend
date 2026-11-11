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

    public Task<Customer?> FindByCodeAsync(string code, Guid? exceptCustomerId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Code.ToUpper() == upper && c.Id != exceptCustomerId, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> ExistingPhonesAsync(IReadOnlyCollection<string> phoneNumbers, CancellationToken cancellationToken) =>
        (await db.Customers.AsNoTracking()
            .Where(c => c.MergedIntoCustomerId == null && c.PhoneNumber != null && phoneNumbers.Contains(c.PhoneNumber))
            .Select(c => c.PhoneNumber!).ToListAsync(cancellationToken)).ToHashSet();

    public void Add(Customer customer) => db.Customers.Add(customer);

    public void Remove(Customer customer) => db.Customers.Remove(customer);
}
