using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICooperativeRepository
{
    /// <summary>The single cooperative of the deployment (created by the bootstrap command), or null before it.</summary>
    Task<Cooperative?> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Another cooperative record already using the tax code.</summary>
    Task<Cooperative?> FindByTaxCodeAsync(string taxCode, Guid exceptCooperativeId, CancellationToken cancellationToken);

    /// <summary>The certifications of the cooperative (FARM-01.1), by name, untracked.</summary>
    Task<IReadOnlyList<CooperativeCertification>> ListCertificationsAsync(Guid cooperativeId, CancellationToken cancellationToken);

    /// <summary>One certification of the cooperative, tracked for editing or removal.</summary>
    Task<CooperativeCertification?> GetCertificationAsync(Guid cooperativeId, Guid certificationId, CancellationToken cancellationToken);

    void AddCertification(CooperativeCertification certification);

    void RemoveCertification(CooperativeCertification certification);
}
