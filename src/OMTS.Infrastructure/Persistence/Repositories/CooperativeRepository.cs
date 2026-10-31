using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CooperativeRepository(OmtsDbContext db) : ICooperativeRepository
{
    public Task<Cooperative?> GetCurrentAsync(CancellationToken cancellationToken) =>
        db.Cooperatives.OrderBy(c => c.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => db.Cooperatives.AnyAsync(cancellationToken);

    public Task<Cooperative?> FindByTaxCodeAsync(string taxCode, Guid exceptCooperativeId, CancellationToken cancellationToken) =>
        db.Cooperatives.AsNoTracking().FirstOrDefaultAsync(c => c.TaxCode == taxCode && c.Id != exceptCooperativeId, cancellationToken);

    public async Task<IReadOnlyList<CooperativeCertification>> ListCertificationsAsync(Guid cooperativeId, CancellationToken cancellationToken) =>
        await db.CooperativeCertifications.AsNoTracking()
            .Where(c => c.CooperativeId == cooperativeId)
            .OrderBy(c => c.Name).ThenBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<CooperativeCertification?> GetCertificationAsync(Guid cooperativeId, Guid certificationId, CancellationToken cancellationToken) =>
        db.CooperativeCertifications.FirstOrDefaultAsync(c => c.Id == certificationId && c.CooperativeId == cooperativeId, cancellationToken);

    public void AddCertification(CooperativeCertification certification) => db.CooperativeCertifications.Add(certification);

    public void RemoveCertification(CooperativeCertification certification) => db.CooperativeCertifications.Remove(certification);
}
