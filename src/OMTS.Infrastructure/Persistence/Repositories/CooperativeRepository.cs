using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CooperativeRepository(OmtsDbContext db) : ICooperativeRepository
{
    public Task<Cooperative?> GetCurrentAsync(CancellationToken cancellationToken) =>
        db.Cooperatives.OrderBy(c => c.CreatedAt).FirstOrDefaultAsync(cancellationToken);
}
