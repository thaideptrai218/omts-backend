using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class AccountRepository(OmtsDbContext db) : IAccountRepository
{
    public Task<Account?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

    public Task<Account?> FindByLoginIdentifierAsync(string username, string? phoneNumber, string? email, CancellationToken cancellationToken) =>
        db.Accounts.FirstOrDefaultAsync(
            a => a.Username == username
                || (phoneNumber != null && a.PhoneNumber == phoneNumber && a.PhoneVerifiedAt != null)
                || (email != null && a.Email == email && a.EmailVerifiedAt != null),
            cancellationToken);

    public Task<bool> UsernameExistsAsync(string username, Guid? exceptAccountId, CancellationToken cancellationToken) =>
        db.Accounts.AnyAsync(a => a.Username == username && a.Id != exceptAccountId, cancellationToken);

    public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
        db.Accounts.FirstOrDefaultAsync(a => a.Username == username, cancellationToken);

    public Task<Account?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken) =>
        db.Accounts.FirstOrDefaultAsync(a => a.PhoneNumber == phoneNumber, cancellationToken);

    public Task<Account?> FindByVerifiedEmailAsync(string email, CancellationToken cancellationToken) =>
        db.Accounts.FirstOrDefaultAsync(a => a.Email == email && a.EmailVerifiedAt != null, cancellationToken);

    public async Task<(IReadOnlyList<Account> Items, int Total)> SearchStaffAsync(
        string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Accounts.AsNoTracking().Where(a => a.SubjectType == SubjectType.Staff);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(a => a.DisplayName.Contains(text) || a.Username.Contains(text) || (a.PhoneNumber != null && a.PhoneNumber.Contains(text)));
        }

        if (statusId is not null)
        {
            query = query.Where(a => a.StatusId == statusId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(a => a.DisplayName).ThenBy(a => a.Username)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add(Account account) => db.Accounts.Add(account);
}
