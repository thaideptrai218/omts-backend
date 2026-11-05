using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class MandatorySmsRecipientQuery(OmtsDbContext db) : IMandatorySmsRecipientQuery
{
    public async Task<IReadOnlyList<MandatorySmsRecipient>> ListAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id) && a.StatusId == StatusIds.Account.Active)
            .Select(a => new { a.Id, a.SubjectType, a.PhoneNumber, a.PhoneVerifiedAt })
            .ToListAsync(cancellationToken);
        var roleCodes = (await db.AccessGrants.AsNoTracking()
            .Where(g => accountIds.Contains(g.AccountId))
            .EffectiveOfFixedRoles()
            .Select(g => new { g.AccountId, g.Role.Code })
            .ToListAsync(cancellationToken))
            .ToLookup(g => g.AccountId, g => g.Code);

        return accounts
            .Select(a => new MandatorySmsRecipient(
                a.Id,
                a.SubjectType,
                roleCodes[a.Id].ToList(),
                a.PhoneVerifiedAt is null || string.IsNullOrWhiteSpace(a.PhoneNumber) ? null : a.PhoneNumber))
            .ToList();
    }
}
