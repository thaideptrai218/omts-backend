using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICooperativeRepository
{
    /// <summary>The single cooperative of the deployment (created by the bootstrap command), or null before it.</summary>
    Task<Cooperative?> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Whether the database holds the cooperative (it is created at deployment).</summary>
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>Another cooperative record already using the tax code.</summary>
    Task<Cooperative?> FindByTaxCodeAsync(string taxCode, Guid exceptCooperativeId, CancellationToken cancellationToken);

    /// <summary>The certifications of the cooperative (FARM-01.1), by name, untracked.</summary>
    Task<IReadOnlyList<CooperativeCertification>> ListCertificationsAsync(Guid cooperativeId, CancellationToken cancellationToken);

    /// <summary>One certification of the cooperative, tracked for editing or removal.</summary>
    Task<CooperativeCertification?> GetCertificationAsync(Guid cooperativeId, Guid certificationId, CancellationToken cancellationToken);

    /// <summary>
    /// Ids of the certifications that may have a reminder due on <paramref name="today"/> (GFR-03): expiring within
    /// <paramref name="expiringSoonDays"/> days or expired, and not yet told. The caller re-checks each one.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListCertificationReminderCandidateIdsAsync(DateOnly today, int expiringSoonDays, CancellationToken cancellationToken);

    void AddCertification(CooperativeCertification certification);

    void RemoveCertification(CooperativeCertification certification);
}
