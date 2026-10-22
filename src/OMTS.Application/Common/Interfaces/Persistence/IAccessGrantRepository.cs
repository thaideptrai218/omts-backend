using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// "Effective" means status Effective, within its validity period on <c>today</c>, and the role still
/// In Use (BR-19); revoked or expired grants are never returned by the effective queries.
/// </summary>
public interface IAccessGrantRepository
{
    Task<bool> HasEffectiveGrantAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken);

    Task<IReadOnlyList<EffectiveGrant>> GetEffectiveGrantsAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>Every grant of the account (history included), with role and zone loaded, newest first.</summary>
    Task<IReadOnlyList<AccessGrant>> ListForAccountAsync(Guid accountId, CancellationToken cancellationToken);

    Task<AccessGrant?> GetAsync(Guid grantId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>An Effective grant of the same account, zone and role whose period overlaps [from, to].</summary>
    Task<AccessGrant?> FindOverlappingAsync(
        Guid accountId, Guid? zoneId, Guid roleId, DateOnly from, DateOnly? to, CancellationToken cancellationToken);

    /// <summary>Effective, not expired grants using the role, with their account loaded.</summary>
    Task<IReadOnlyList<AccessGrant>> GetEffectiveForRoleAsync(Guid roleId, DateOnly today, CancellationToken cancellationToken);

    void Add(AccessGrant grant);
}
