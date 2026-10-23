using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class AccountRepository(OmtsDbContext db) : IAccountRepository
{
    public Task<Account?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

    public Task<Account?> FindByLoginIdentifierAsync(string identifier, CancellationToken cancellationToken) =>
        db.Accounts.FirstOrDefaultAsync(
            a => a.Username == identifier
                || (a.PhoneNumber == identifier && a.PhoneVerifiedAt != null)
                || (a.Email == identifier && a.EmailVerifiedAt != null),
            cancellationToken);

    public void Add(Account account) => db.Accounts.Add(account);
}
