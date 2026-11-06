using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CooperativeOwnerQuery(OmtsDbContext db) : ICooperativeOwnerQuery
{
    public async Task<IReadOnlyList<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);
}
